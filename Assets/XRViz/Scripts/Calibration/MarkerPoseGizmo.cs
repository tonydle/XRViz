using UnityEngine;

namespace Unity.Robotics
{
    // Draws the detected tag where the calibration thinks it is: a square the size of the real
    // marker, plus its three axes.
    //
    // This is the check on the one number nobody else can check. Everything downstream of the
    // detector is verifiable maths, but the head-to-camera offset - where the passthrough lens
    // sits relative to tracking space - is read from the device or guessed, and an error in it
    // moves the whole result by a constant amount that no residual or reprojection figure will
    // reveal. Drawn in the room, it is obvious: the square either sits on the printed tag or it
    // does not.
    //
    // Built as a Mesh under a MeshFilter for the same reason LaserScanVisualizer is (see
    // CLAUDE.md): the Transform then places it, and it survives being moved.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MarkerPoseGizmo : MonoBehaviour
    {
        [SerializeField] private float _axisLength = 0.05f;

        private static readonly Color k_OutlineColor = new Color(1f, 0.85f, 0.1f);
        private static readonly Color k_XAxisColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color k_YAxisColor = new Color(0.35f, 1f, 0.35f);
        private static readonly Color k_ZAxisColor = new Color(0.35f, 0.6f, 1f);

        private Mesh _mesh;
        private float _builtForSize = -1f;

        private void Awake()
        {
            _mesh = new Mesh { name = "MarkerPoseGizmo" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().mesh = _mesh;

            var renderer = GetComponent<MeshRenderer>();
            if (renderer.sharedMaterial == null)
            {
                // Lit shaders render near-black under passthrough, and Unlit/Color drops vertex
                // colours - the same constraint every visualisation in this project has
                Shader shader = Shader.Find("XRViz/VertexColorUnlit")
                    ?? Shader.Find("Sprites/Default")
                    ?? Shader.Find("Unlit/Color");
                renderer.sharedMaterial = new Material(shader) { name = "MarkerPoseGizmo" };
            }

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            GetComponent<MeshRenderer>().enabled = visible;
        }

        // markerSizeMetres is the side of the printed black square, so the drawn outline can be
        // compared against the real thing one to one
        public void Show(Vector3 position, Quaternion rotation, float markerSizeMetres)
        {
            transform.SetPositionAndRotation(position, rotation);

            if (!Mathf.Approximately(_builtForSize, markerSizeMetres))
            {
                Rebuild(markerSizeMetres);
                _builtForSize = markerSizeMetres;
            }

            SetVisible(true);
        }

        private void Rebuild(float markerSizeMetres)
        {
            float half = 0.5f * markerSizeMetres;

            // The gizmo's local axes match MarkerPose: +Z out of the printed face, +Y towards
            // the top of the print, so the square lies in the local XY plane
            var vertices = new[]
            {
                new Vector3(-half, half, 0f),
                new Vector3(half, half, 0f),
                new Vector3(half, -half, 0f),
                new Vector3(-half, -half, 0f),

                Vector3.zero, new Vector3(_axisLength, 0f, 0f),
                Vector3.zero, new Vector3(0f, _axisLength, 0f),
                Vector3.zero, new Vector3(0f, 0f, _axisLength),
            };

            var colors = new[]
            {
                k_OutlineColor, k_OutlineColor, k_OutlineColor, k_OutlineColor,
                k_XAxisColor, k_XAxisColor,
                k_YAxisColor, k_YAxisColor,
                k_ZAxisColor, k_ZAxisColor,
            };

            var indices = new[]
            {
                0, 1, 1, 2, 2, 3, 3, 0,   // the marker outline
                4, 5, 6, 7, 8, 9,         // X, Y, Z
            };

            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.colors = colors;
            _mesh.SetIndices(indices, MeshTopology.Lines, 0);
            // Generous bounds: the mesh is tiny and recentred by the Transform, and a tight
            // bounding box on a line mesh gets frustum-culled at glancing angles
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (markerSizeMetres + _axisLength) * 2f);
        }

        private void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
