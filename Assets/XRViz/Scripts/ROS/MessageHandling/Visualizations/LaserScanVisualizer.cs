using UnityEngine;

namespace Unity.Robotics
{
    // Draws a LaserScan as a cloud of small cubes.
    //
    // Built as a Mesh on this GameObject's MeshFilter rather than with Graphics.DrawProcedural
    // (which is how PointCloudRosGPU_PointCloud2 does it). That matters: DrawProcedural renders
    // in world space and ignores the GameObject's Transform, so a PlacementHandle can't move it.
    // A mesh under a MeshRenderer is transformed by Unity for free, so the scan can be picked up
    // and put down in the room like everything else.
    //
    // Cubes rather than points or quads because a scan has to stay readable from any angle in
    // VR: MeshTopology.Points draws single pixels, and flat quads in the scan plane vanish when
    // viewed edge-on, which is exactly where your head usually is relative to a floor-level lidar.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LaserScanVisualizer : MonoBehaviour
    {
        [SerializeField] private RosSubscriberLaserScan _scanSub;
        [SerializeField] private float _pointSize = 0.02f;

        // Beyond this the scan is decimated by taking every Nth point. A 2D lidar is typically
        // 360-1800 beams so this rarely bites, but a high-rate multi-echo unit can exceed it.
        [SerializeField] private int _maxPoints = 4096;

        // Blank the scan once it stops arriving. A lidar that drops out - or an endpoint that
        // drops the connection - otherwise leaves its final sweep hanging in the room looking
        // exactly like live data. Set to 0 to keep the last sweep indefinitely.
        [SerializeField] private float _staleAfterSeconds = 3f;

        [SerializeField] private bool _colorByRange = true;
        [SerializeField] private Color _nearColor = new Color(0.2f, 1f, 0.4f);
        [SerializeField] private Color _farColor = new Color(0.1f, 0.4f, 1f);

        // Left null the visualiser makes its own unlit material - passthrough MR has no useful
        // scene lighting, so a lit shader would just render the scan near-black
        [SerializeField] private Material _material;

        private Mesh _mesh;
        private Vector3[] _vertices;
        private Color[] _colors;
        private int[] _triangles;
        private int _meshCapacity = -1;
        private int _drawnCount;

        // A unit cube's 8 corners, and the 12 triangles indexing them. Flat-shaded normals would
        // need 24 vertices; with an unlit material they'd buy nothing.
        static readonly Vector3[] k_CubeCorners =
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f),
        };

        static readonly int[] k_CubeIndices =
        {
            0,2,1, 0,3,2, // back
            1,6,5, 1,2,6, // right
            5,7,4, 5,6,7, // front
            4,3,0, 4,7,3, // left
            3,6,2, 3,7,6, // top
            4,1,5, 4,0,1, // bottom
        };

        private void Start()
        {
            _mesh = new Mesh { name = "LaserScan" };
            _mesh.MarkDynamic();
            // A scan can easily exceed 65535 vertices once each point is a cube
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            GetComponent<MeshFilter>().mesh = _mesh;

            var renderer = GetComponent<MeshRenderer>();
            if (_material != null)
                renderer.material = _material;
            else
                renderer.material = new Material(FindUnlitShader()) { color = Color.white };
        }

        private static Shader FindUnlitShader()
        {
            // Must read Mesh.colors or the range gradient renders flat - the built-in
            // Unlit/Color ignores vertex colours, so it's only a last resort here
            Shader shader = Shader.Find("XRViz/VertexColorUnlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            return shader;
        }

        // Wipes the drawn scan and drops the subscriber's parsed points with it - clearing only
        // the mesh would let the very next frame rebuild it from the same cached message.
        // Wired to the ROS control panel's "Clear Scan" button.
        public void Clear()
        {
            if (_scanSub != null)
                _scanSub.ClearData();
            ClearMesh();
        }

        private void ClearMesh()
        {
            if (_drawnCount == 0)
                return;
            _drawnCount = 0;
            if (_mesh != null)
                _mesh.Clear();
        }

        private void Update()
        {
            if (_scanSub == null)
                return;

            if (_staleAfterSeconds > 0f)
            {
                float lastArrival = _scanSub.GetLastMessageRealtime();
                if (lastArrival < 0f || Time.realtimeSinceStartup - lastArrival > _staleAfterSeconds)
                {
                    ClearMesh();
                    return;
                }
            }

            if (!_scanSub.isReady())
                return;

            int available = _scanSub.GetPointCount();
            int stride = available > _maxPoints ? Mathf.CeilToInt(available / (float)_maxPoints) : 1;
            int drawn = stride == 1 ? available : Mathf.CeilToInt(available / (float)stride);

            EnsureCapacity(drawn);
            BuildMesh(drawn, stride);
        }

        private void EnsureCapacity(int pointCount)
        {
            if (_meshCapacity >= pointCount)
                return;

            _meshCapacity = Mathf.Max(pointCount, 256);
            _vertices = new Vector3[_meshCapacity * 8];
            _colors = new Color[_meshCapacity * 8];
            _triangles = new int[_meshCapacity * 36];

            // Index layout never changes - only the vertex positions do - so build it once here
            // rather than every frame
            for (int p = 0; p < _meshCapacity; p++)
            {
                int vertexBase = p * 8;
                int indexBase = p * 36;
                for (int i = 0; i < k_CubeIndices.Length; i++)
                    _triangles[indexBase + i] = vertexBase + k_CubeIndices[i];
            }
        }

        private void BuildMesh(int drawn, int stride)
        {
            Vector3[] points = _scanSub.GetPoints();
            float[] ranges = _scanSub.GetRanges();
            float rangeMin = _scanSub.GetRangeMin();
            float rangeSpan = Mathf.Max(_scanSub.GetRangeMax() - rangeMin, 0.001f);
            float half = _pointSize;

            for (int p = 0; p < drawn; p++)
            {
                int source = p * stride;
                Vector3 center = points[source];
                Color color = _colorByRange
                    ? Color.Lerp(_nearColor, _farColor, Mathf.Clamp01((ranges[source] - rangeMin) / rangeSpan))
                    : _nearColor;

                int vertexBase = p * 8;
                for (int c = 0; c < 8; c++)
                {
                    _vertices[vertexBase + c] = center + k_CubeCorners[c] * half;
                    _colors[vertexBase + c] = color;
                }
            }

            // Collapse the unused tail onto a single point so leftover cubes from a denser
            // previous scan render as nothing instead of lingering at the origin
            for (int p = drawn; p < _meshCapacity; p++)
            {
                int vertexBase = p * 8;
                for (int c = 0; c < 8; c++)
                    _vertices[vertexBase + c] = Vector3.zero;
            }

            _mesh.Clear();
            _mesh.vertices = _vertices;
            _mesh.colors = _colors;
            _mesh.triangles = _triangles;
            _mesh.RecalculateBounds();
            _drawnCount = drawn;
        }

        private void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
