using UnityEngine;

namespace Unity.Robotics
{
    // Places a visualisation at its data's TF frame instead of wherever a hand left it.
    //
    // Manual placement (a PlacementHandle you grab) and TF placement answer two different
    // questions. The handle answers "where in this room do I want to see this?", which is what you
    // want when nothing is calibrated and you just need the cloud somewhere you can look at it.
    // TF answers "where is this, according to the robot?", which is what you want the moment the
    // robot's own frames are trustworthy - then the laser sits at the laser's frame and the cloud
    // at the camera's, all consistent with each other, and only ONE thing needs aligning with the
    // real room: the TF origin handle (RosTfTree), which every frame is measured out from.
    //
    // Both are kept because both are useful: this toggles at runtime, per visualisation from the
    // TF Anchors panel or all at once from the control panel.
    [DefaultExecutionOrder(-100)] // before DepthImagePointCloud bakes localToWorldMatrix in its LateUpdate
    public class TfAnchor : MonoBehaviour
    {
        // The handle whose target this anchor drives. Left null the anchor moves its own
        // Transform - fine for a plain visualisation, but a robot root needs the handle's
        // ArticulationBody path (TeleportRoot), so keep them paired where one exists.
        [SerializeField] private PlacementHandle _handle;

        // A subscriber implementing IRosFrameSource, so the frame follows the topic when it is
        // retargeted from the headset. Serialized as MonoBehaviour because Unity can't serialize
        // interface references; validated in Awake.
        [SerializeField] private MonoBehaviour _frameSource;

        // Used when there is no frame source, or before its first message arrives. Also the only
        // way to anchor something whose frame isn't in any message - the robot's own base link.
        [SerializeField] private string _frameId = "";

        // Off by default, so a scene behaves exactly as it did before anyone touches the toggle
        [SerializeField] private bool _anchorToTf = false;

        public enum OpticalFrameHandling
        {
            // Frames named *_optical_frame get the correction; everything else doesn't
            Auto,
            Never,
            Always,
        }

        // A camera optical frame is right-down-forward (REP 103/145), not FLU, and the RGBD
        // pipeline draws its points in that convention (see DepthImagePointCloudGPU.compute and
        // the exception noted in CLAUDE.md). The TF tree converts transforms as FLU like
        // everything else, so an optical frame needs the difference rotated back out - see
        // k_OpticalCorrection.
        [SerializeField] private OpticalFrameHandling _opticalFrame = OpticalFrameHandling.Auto;

        // What the TF Anchors panel calls this. Defaults to the GameObject's name.
        [SerializeField] private string _label = "";

        // The two conventions differ by an axis permutation, not a flip. Converting an FLU
        // transform gives Unity axes via (x,y,z)_ros -> (-y,z,x)_unity, while the optical
        // pipeline emits its points as (x,-y,z) - Unity's own camera convention. Composing one
        // with the inverse of the other leaves the cyclic permutation x<-y<-z<-x, which is a
        // rotation of -120 degrees about (1,1,1). Post-multiplying it onto the TF rotation makes a
        // forward-looking camera come out with identity rotation relative to its parent link,
        // which is the sanity check to use if this ever looks wrong.
        private static readonly Quaternion k_OpticalCorrection =
            Quaternion.AngleAxis(-120f, new Vector3(1f, 1f, 1f).normalized);

        public enum AnchorStatus
        {
            Manual,          // the handle owns the pose
            Anchored,        // placed from TF this frame
            NoTfTree,        // no RosTfTree in the scene
            NoFrameId,       // nothing has told us which frame the data is in yet
            FrameNotInTf,    // we know the frame, the TF tree doesn't
        }

        private IRosFrameSource _source;
        private AnchorStatus _status = AnchorStatus.Manual;

        public bool AnchorToTf => _anchorToTf;
        public AnchorStatus Status => _status;
        public string Label => string.IsNullOrEmpty(_label) ? gameObject.name : _label;

        // Whichever frame we'd place by right now: live from the topic if we have one, else the
        // serialized fallback
        public string FrameId
        {
            get
            {
                string live = _source != null ? _source.FrameId : null;
                return string.IsNullOrEmpty(live) ? _frameId : live;
            }
        }

        private void Awake()
        {
            if (_frameSource != null)
            {
                _source = _frameSource as IRosFrameSource;
                if (_source == null)
                    Debug.LogWarning($"[XRViz] {_frameSource.GetType().Name} on '{_frameSource.name}' does " +
                        $"not implement {nameof(IRosFrameSource)}; '{name}' will fall back to its " +
                        "serialized frame id.", this);
            }
        }

        private void Start()
        {
            // Apply the serialized starting mode once the handle exists to be suspended
            if (_anchorToTf)
                ApplyMode();
        }

        public void SetAnchorToTf(bool anchorToTf)
        {
            if (_anchorToTf == anchorToTf)
                return;

            _anchorToTf = anchorToTf;
            ApplyMode();
        }

        public void ToggleAnchorToTf()
        {
            SetAnchorToTf(!_anchorToTf);
        }

        private void ApplyMode()
        {
            if (_handle != null)
                _handle.SetSuspended(_anchorToTf);

            if (!_anchorToTf)
                _status = AnchorStatus.Manual;
            else
                Place(); // don't wait a frame to show whether the frame actually resolves
        }

        private void LateUpdate()
        {
            if (!_anchorToTf)
                return;

            Place();
        }

        // Leaves the object exactly where it is whenever the pose can't be resolved. A missing
        // frame must not read as "the sensor is at the origin" - that would put a cloud
        // convincingly in the wrong place, which is worse than leaving it where you last saw it.
        private void Place()
        {
            var tree = RosTfTree.Instance;
            if (tree == null)
            {
                _status = AnchorStatus.NoTfTree;
                return;
            }

            string frame = FrameId;
            if (string.IsNullOrEmpty(frame))
            {
                _status = AnchorStatus.NoFrameId;
                return;
            }

            if (!tree.TryGetWorldPose(frame, out var position, out var rotation))
            {
                _status = AnchorStatus.FrameNotInTf;
                return;
            }

            if (UseOpticalCorrection(frame))
                rotation *= k_OpticalCorrection;

            if (_handle != null)
                _handle.SetTargetPose(position, rotation);
            else
                transform.SetPositionAndRotation(position, rotation);

            _status = AnchorStatus.Anchored;
        }

        private bool UseOpticalCorrection(string frame)
        {
            switch (_opticalFrame)
            {
                case OpticalFrameHandling.Always:
                    return true;
                case OpticalFrameHandling.Never:
                    return false;
                default:
                    return frame.EndsWith("_optical_frame", System.StringComparison.Ordinal);
            }
        }
    }
}
