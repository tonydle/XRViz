using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Robotics
{
    // Draws a sensor_msgs/PointCloud2 as camera-facing squares, in the cloud's own frame, under
    // this GameObject's Transform.
    //
    // Built as a Mesh on a MeshFilter rather than with Graphics.DrawProcedural, for the reason
    // CLAUDE.md gives: DrawProcedural renders in world space and ignores the Transform, so a
    // PlacementHandle can't move it and a TfAnchor can't place it. That is exactly the trap
    // PointCloudRosGPU_PointCloud2 fell into, and it is why this exists alongside it.
    //
    // Four vertices per point, all at the same position, with the quad corner in UV0 - the
    // expansion happens in XRViz/PointCloudMeshBillboard's vertex shader. So the mesh is rebuilt
    // only when a cloud actually arrives, not every frame to re-face the squares, and the cost
    // per frame is a draw call.
    //
    // For an RGBD camera, DepthImagePointCloud is the better path: reconstructing on the GPU from
    // depth + CameraInfo moves far less over the socket than 32 bytes a point and costs no CPU.
    // This is for clouds that only exist as PointCloud2 - lidar, or a node that has already fused
    // something - and for looking at exactly what a /points topic really contains.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class PointCloud2Visualizer : MonoBehaviour, IClearableVisualization
    {
        [SerializeField] private RosSubscriberPointCloud2 _cloudSub;

        [Tooltip("Side of each point's square, in metres.")]
        [SerializeField] private float _pointSize = 0.012f;

        // Blank the cloud once it stops arriving. A sensor that drops out - or an endpoint that
        // drops the connection - otherwise leaves its last cloud hanging in the room looking
        // exactly like live data. 0 keeps the last cloud indefinitely.
        [SerializeField] private float _staleAfterSeconds = 3f;

        // Left null the visualiser makes its own material from the billboard shader. A lit shader
        // would render the cloud near-black under passthrough, and Unlit/Color drops the vertex
        // colours the cloud is drawn with.
        [SerializeField] private Material _material;

        private const string k_ShaderName = "XRViz/PointCloudMeshBillboard";

        // Quad corners, and the two triangles indexing them. Counter-clockwise; Cull is off in
        // the shader anyway, since a billboard has no meaningful facing.
        private static readonly Vector2[] k_Corners =
        {
            new Vector2(-1f, -1f), new Vector2(1f, -1f),
            new Vector2(1f, 1f), new Vector2(-1f, 1f),
        };

        private static readonly int[] k_QuadIndices = { 0, 2, 1, 0, 3, 2 };

        private Mesh _mesh;
        private Vector3[] _vertices;
        private Vector2[] _uvs;
        private Color[] _colors;
        private int[] _indices;
        private int _capacity = -1;
        private int _drawnPoints;

        // Which cloud is already on screen. The subscriber stamps each parse with its arrival
        // time, so this is how the visualiser knows a NEW cloud arrived rather than rebuilding
        // the same mesh every frame.
        private float _builtForRealtime = -1f;

        private void Start()
        {
            _mesh = new Mesh { name = "PointCloud2" };
            _mesh.MarkDynamic();
            // Four vertices a point passes 65535 at 16384 points, which is a small cloud
            _mesh.indexFormat = IndexFormat.UInt32;
            GetComponent<MeshFilter>().mesh = _mesh;

            var meshRenderer = GetComponent<MeshRenderer>();
            if (_material != null)
            {
                meshRenderer.material = _material;
            }
            else
            {
                Shader shader = Shader.Find(k_ShaderName);
                if (shader == null)
                {
                    // Named rather than generic: a missing shader here draws nothing at all, and
                    // "the point cloud doesn't work" is a long way from "the shader didn't build"
                    Debug.LogError($"[XRViz] Shader '{k_ShaderName}' not found, so the point cloud " +
                        "has nothing to draw with. It must also be in Project Settings > Graphics > " +
                        "Always Included Shaders (or referenced by a material in the scene) to " +
                        "survive a player build.", this);
                    enabled = false;
                    return;
                }
                meshRenderer.material = new Material(shader) { name = "PointCloud2 (runtime)" };
            }

            meshRenderer.material.SetFloat("_Size", _pointSize);
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        // Wipes the drawn cloud and drops the subscriber's parsed points with it - clearing only
        // the mesh would let the next frame rebuild it from the same cached message. Found
        // automatically by the control panel's Clear Data button.
        public void Clear()
        {
            if (_cloudSub != null)
                _cloudSub.ClearData();
            ClearMesh();
        }

        private void ClearMesh()
        {
            if (_drawnPoints == 0)
                return;
            _drawnPoints = 0;
            _builtForRealtime = -1f;
            if (_mesh != null)
                _mesh.Clear();
        }

        private void Update()
        {
            if (_cloudSub == null)
                return;

            float arrival = _cloudSub.GetLastMessageRealtime();

            if (_staleAfterSeconds > 0f)
            {
                if (arrival < 0f || Time.realtimeSinceStartup - arrival > _staleAfterSeconds)
                {
                    ClearMesh();
                    return;
                }
            }

            if (!_cloudSub.isReady())
                return;

            // Same cloud as last frame: the mesh already holds it, and the squares are turned
            // towards the eye in the vertex shader rather than here
            if (arrival == _builtForRealtime)
                return;

            _builtForRealtime = arrival;
            BuildMesh(_cloudSub.GetPointCount());
        }

        private void EnsureCapacity(int pointCount)
        {
            if (_capacity >= pointCount)
                return;

            _capacity = Mathf.Max(pointCount, 1024);
            _vertices = new Vector3[_capacity * 4];
            _uvs = new Vector2[_capacity * 4];
            _colors = new Color[_capacity * 4];
            _indices = new int[_capacity * 6];

            // Corners and indices never change - only the positions and colours do - so they are
            // written once here rather than per cloud
            for (int p = 0; p < _capacity; p++)
            {
                int vertexBase = p * 4;
                int indexBase = p * 6;
                for (int c = 0; c < 4; c++)
                    _uvs[vertexBase + c] = k_Corners[c];
                for (int i = 0; i < k_QuadIndices.Length; i++)
                    _indices[indexBase + i] = vertexBase + k_QuadIndices[i];
            }
        }

        private void BuildMesh(int pointCount)
        {
            if (pointCount <= 0)
            {
                ClearMesh();
                return;
            }

            EnsureCapacity(pointCount);

            Vector3[] points = _cloudSub.GetLatestPoints();
            Color[] colors = _cloudSub.GetLatestColors();

            for (int p = 0; p < pointCount; p++)
            {
                Vector3 position = points[p];
                Color color = colors[p];
                int vertexBase = p * 4;
                for (int c = 0; c < 4; c++)
                {
                    _vertices[vertexBase + c] = position;
                    _colors[vertexBase + c] = color;
                }
            }

            // Only the live prefix is uploaded - the arrays are capacity-sized and their tail is
            // whatever the last, larger cloud left there
            _mesh.Clear();
            _mesh.indexFormat = IndexFormat.UInt32;
            _mesh.SetVertices(_vertices, 0, pointCount * 4);
            _mesh.SetUVs(0, _uvs, 0, pointCount * 4);
            _mesh.SetColors(_colors, 0, pointCount * 4);
            _mesh.SetIndices(_indices, 0, pointCount * 6, MeshTopology.Triangles, 0, false);

            // Bounds come from the point positions, but the squares are grown around them in the
            // vertex shader - so a cloud sitting exactly on the frustum edge would be culled a
            // little early without this
            _mesh.RecalculateBounds();
            var bounds = _mesh.bounds;
            bounds.Expand(_pointSize);
            _mesh.bounds = bounds;

            _drawnPoints = pointCount;
        }

        private void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }

        private void OnValidate()
        {
            // So dragging the size in the Inspector during play does something
            if (!Application.isPlaying)
                return;
            var meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.material != null)
                meshRenderer.material.SetFloat("_Size", _pointSize);
        }
    }
}
