using UnityEngine;

namespace Unity.Robotics
{
    // Point cloud from an RGBD camera: a depth image, a colour image, and the depth camera's
    // intrinsics. Reconstruction runs on the GPU; the result is drawn as camera-facing squares.
    //
    // THIS GAMEOBJECT IS THE CLOUD'S ORIGIN. Its Transform is handed to the compute shader as
    // originTransform, and every point is projected out of it - the camera's optical centre sits
    // exactly on this object, the cloud extends along its +Z, and moving it (with a
    // PlacementHandle) carries the whole cloud along. That is the one thing that makes an RGBD
    // cloud usable in MR: you park the origin where the real camera is in the room and the
    // virtual geometry lands on top of the real geometry.
    //
    // Why this exists alongside PointCloudRosGPU, which reconstructs the same way:
    //   - it takes raw sensor_msgs/Image, which is what a simulator publishes, instead of
    //     CompressedImage (which needs image_transport republishers and quantises depth to
    //     16-bit PNG)
    //   - depth scale comes from the message encoding, so 32FC1 metres works as well as 16UC1
    //     millimetres - a sim's depth is exact float and there is no reason to round it
    //   - it uses the optical-frame convention rather than FLU (see the compute shader)
    //   - it draws with a stereo-aware shader and no geometry shader, so it renders to both
    //     eyes on Quest at a sane frame rate
    //
    // BANDWIDTH IS THE PRACTICAL LIMIT, not the GPU. Raw 640x480 rgb8 is 900 KB a frame and
    // 640x480 32FC1 depth is 1.2 MB; at 30 Hz that is ~60 MB/s over a TCP socket to a headset on
    // wifi, which will not happen. Throttle on the ROS side (a lower publish rate, or a smaller
    // camera resolution) rather than expecting Unity to keep up - _decimation below thins the
    // cloud that gets drawn, but the bytes have already crossed the network by then.
    public class DepthImagePointCloud : MonoBehaviour, IClearableVisualization
    {
        [SerializeField] private RosSubscriberImage _depthSub;
        [SerializeField] private RosSubscriberImage _colorSub;

        // Intrinsics of the DEPTH camera. A registered RGBD pair publishes the same intrinsics
        // for both, but if they differ it is the depth ones that back-project correctly.
        [SerializeField] private RosSubscriberCameraInfo _depthInfoSub;

        [SerializeField] private ComputeShader _computeShader;

        // Left null a material is made from XRViz/PointCloudBillboard
        [SerializeField] private Material _material;

        [SerializeField] private float _pointSize = 0.012f;

        // Keep one pixel in N per axis. 2 quarters the point count, which is usually the
        // difference between comfortable and not on Quest - the GPU cost is per drawn point.
        [SerializeField, Range(1, 8)] private int _decimation = 2;

        [SerializeField] private float _minRange = 0.15f;
        [SerializeField] private float _maxRange = 8f;

        // Stop drawing once depth stops arriving. Same reasoning as LaserScanVisualizer: a
        // frozen cloud looks exactly like a live one. 0 keeps the last frame indefinitely.
        [SerializeField] private float _staleAfterSeconds = 3f;

        // Draw depth-only (flat grey) when no colour image has arrived, rather than nothing.
        // Usually what you want while bringing a camera up - it separates "no depth" from
        // "no colour", which otherwise look identical.
        [SerializeField] private bool _drawWithoutColor = true;

        private const string k_KernelName = "DepthImagePointCloud";
        private const string k_ShaderName = "XRViz/PointCloudBillboard";

        private ComputeShader _compute;
        private Material _materialInstance;
        private ComputeBuffer _positionBuffer;
        private ComputeBuffer _colorBuffer;

        private int _kernel = -1;
        private int _pointCount;
        private int _bufferWidth;
        private int _bufferHeight;
        private bool _hasData;
        private bool _missingInfoLogged;

        private void Start()
        {
            if (_computeShader == null)
            {
                Debug.LogError($"[XRViz] {name}: no compute shader assigned; assign " +
                    "Assets/XRViz/Shaders/DepthImagePointCloudGPU.compute. Disabling.", this);
                enabled = false;
                return;
            }

            // Instanced so two clouds in one scene don't overwrite each other's bound textures
            // and buffers - ComputeShader state is per-asset, not per-dispatch
            _compute = Instantiate(_computeShader);
            _kernel = _compute.FindKernel(k_KernelName);

            if (_material != null)
            {
                _materialInstance = new Material(_material);
            }
            else
            {
                var shader = Shader.Find(k_ShaderName);
                if (shader == null)
                {
                    Debug.LogError($"[XRViz] {name}: shader '{k_ShaderName}' not found and no " +
                        "material assigned. Disabling.", this);
                    enabled = false;
                    return;
                }
                _materialInstance = new Material(shader);
            }
        }

        private void LateUpdate()
        {
            if (!UpdateCloud())
                return;

            _materialInstance.SetFloat("_Size", _pointSize);

            // Bounds are only a culling hint, and the cloud is rebuilt around this transform
            // every frame, so a box of _maxRange about the origin always contains it
            var bounds = new Bounds(transform.position, Vector3.one * (_maxRange * 2f));

            // Six vertices per point, expanded in the vertex shader from SV_VertexID - see
            // PointCloudBillboard.shader for why this isn't a geometry shader
            Graphics.DrawProcedural(_materialInstance, bounds, MeshTopology.Triangles, _pointCount * 6, 1);
        }

        // Returns whether there is anything to draw this frame. Re-dispatches every frame rather
        // than only on a new image: the compute shader bakes this Transform into the positions,
        // so a cloud that only rebuilt on new data would be left behind whenever the placement
        // handle moved between frames. A dispatch over a decimated image is a rounding error
        // next to the draw.
        private bool UpdateCloud()
        {
            if (_depthSub == null || !_depthSub.isReady())
                return _hasData = false;

            if (_staleAfterSeconds > 0f)
            {
                float lastArrival = _depthSub.GetLastMessageRealtime();
                if (lastArrival < 0f || Time.realtimeSinceStartup - lastArrival > _staleAfterSeconds)
                    return _hasData = false;
            }

            if (_depthInfoSub == null || !_depthInfoSub.isReady())
            {
                if (!_missingInfoLogged)
                {
                    _missingInfoLogged = true;
                    Debug.LogWarning($"[XRViz] {name}: depth is arriving but no CameraInfo has. " +
                        "Without intrinsics there is nothing to back-project with - check the " +
                        "camera_info topic alongside the depth topic.", this);
                }
                return _hasData = false;
            }

            var depthTexture = _depthSub.GetLatestTexture2D();
            if (depthTexture == null || depthTexture.width < 1 || depthTexture.height < 1)
                return _hasData = false;

            bool hasColor = _colorSub != null && _colorSub.isReady();
            if (!hasColor && !_drawWithoutColor)
                return _hasData = false;

            var colorTexture = hasColor ? _colorSub.GetLatestTexture2D() : Texture2D.whiteTexture;

            int decimation = Mathf.Max(1, _decimation);
            int outWidth = Mathf.Max(1, depthTexture.width / decimation);
            int outHeight = Mathf.Max(1, depthTexture.height / decimation);
            EnsureBuffers(outWidth, outHeight);

            float[] intrinsics = _depthInfoSub.GetCameraNecessaryInfo(); // [cx, cy, fx, fy]
            _compute.SetVector("depthIntrinsics",
                new Vector4(intrinsics[2], intrinsics[3], intrinsics[0], intrinsics[1]));

            _compute.SetTexture(_kernel, "inDepth", depthTexture);
            _compute.SetTexture(_kernel, "inColor", colorTexture);

            _compute.SetInt("depthWidth", depthTexture.width);
            _compute.SetInt("depthHeight", depthTexture.height);
            _compute.SetInt("colorWidth", colorTexture.width);
            _compute.SetInt("colorHeight", colorTexture.height);
            _compute.SetInt("outWidth", outWidth);
            _compute.SetInt("outHeight", outHeight);
            _compute.SetInt("stride", decimation);
            _compute.SetInt("hasColor", hasColor ? 1 : 0);
            _compute.SetInt("colorIsBgr", hasColor && _colorSub.IsBgr() ? 1 : 0);
            _compute.SetFloat("depthMetersPerUnit", _depthSub.GetDepthMetersPerUnit());
            _compute.SetFloat("minRange", _minRange);
            _compute.SetFloat("maxRange", _maxRange);
            _compute.SetMatrix("originTransform", transform.localToWorldMatrix);

            // Ceil, with the kernel bounds-checking the overhang - the old pipeline required
            // the image to divide by 8 exactly, which quietly dropped the last rows otherwise
            _compute.Dispatch(_kernel,
                Mathf.CeilToInt(outWidth / 8f), Mathf.CeilToInt(outHeight / 8f), 1);

            return _hasData = true;
        }

        private void EnsureBuffers(int width, int height)
        {
            if (_positionBuffer != null && _bufferWidth == width && _bufferHeight == height)
                return;

            ReleaseBuffers();

            _bufferWidth = width;
            _bufferHeight = height;
            _pointCount = width * height;

            _positionBuffer = new ComputeBuffer(_pointCount, sizeof(float) * 3);
            _colorBuffer = new ComputeBuffer(_pointCount, sizeof(float) * 4);

            _compute.SetBuffer(_kernel, "positionsOut", _positionBuffer);
            _compute.SetBuffer(_kernel, "colorsOut", _colorBuffer);

            _materialInstance.SetBuffer("vertexPosition", _positionBuffer);
            _materialInstance.SetBuffer("vertexColor", _colorBuffer);
        }

        // Stop drawing and drop what the subscribers have parsed, so the cloud stays gone
        // instead of being rebuilt from the same cached images on the next frame
        public void Clear()
        {
            _hasData = false;
            if (_depthSub != null)
                _depthSub.ClearData();
            if (_colorSub != null)
                _colorSub.ClearData();
        }

        private void ReleaseBuffers()
        {
            if (_positionBuffer != null)
            {
                _positionBuffer.Release();
                _positionBuffer = null;
            }
            if (_colorBuffer != null)
            {
                _colorBuffer.Release();
                _colorBuffer = null;
            }
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
            if (_materialInstance != null)
                Destroy(_materialInstance);
            if (_compute != null)
                Destroy(_compute);
        }
    }
}
