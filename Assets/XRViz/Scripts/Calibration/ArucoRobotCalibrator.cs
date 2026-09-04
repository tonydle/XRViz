using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.Robotics
{
    // Snaps the virtual robot onto the real one by finding an ArUco tag on the real robot's
    // chassis in the passthrough camera.
    //
    // The flow the button drives:
    //   press          -> open the camera, search for the tag
    //   tag found      -> move the robot there NOW and wait for the user to agree
    //   agreed         -> keep it, close the camera
    //   not agreed     -> put the robot back exactly where it was
    //   nothing found  -> after the timeout, say what was missing rather than just "failed"
    //
    // Showing the result by actually moving the robot, rather than by drawing a ghost of it, is
    // deliberate: the question being asked is "does the virtual arm line up with the real one?",
    // and the only honest way to answer it is to look at both through the passthrough at once.
    // Cancelling restores the previous pose exactly, so agreeing is the only thing that commits.
    //
    // This is the counterpart to MRRobotRegistrationTool, which does the same job by having the
    // user place three point pairs by hand. That still works and is still the fallback when
    // there is no tag on the robot - or when the tag cannot be seen from anywhere comfortable.
    public class ArucoRobotCalibrator : MonoBehaviour
    {
        public enum CalibrationState
        {
            Idle,
            Searching,
            AwaitingConfirmation,
            Applied,
            TimedOut,
            Unavailable,     // no passthrough camera here - Editor or Quest Link
        }

        // How the vertical axis is decided. A single planar tag pins horizontal position and
        // yaw well; height is the weakest thing it gives, because it depends on the tag's
        // measured size, the lens pose and the tag's own mounting height all being right at
        // once. Two of those are guesses, so by default height is not taken from the tag at all.
        public enum VerticalMode
        {
            // Keep the robot at the height it already has, and take only the horizontal
            // position and yaw from the tag. Needs no measurement of where the tag sits on the
            // chassis, which is the number nobody has to hand.
            KeepRobotHeight,

            // Place the base below the tag by Tag Height Above Base. Correct if that number is
            // measured; a robot floating by the tag's height if it was left at zero.
            FromTagHeight,
        }

        public enum ApplyMode
        {
            // Move whichever thing actually owns the robot's pose: the TF origin when the robot
            // is anchored to TF, the robot itself when it is placed by hand
            Auto,
            MoveRobot,
            MoveTfOrigin,
        }

        [Header("Camera and detection")]
        [SerializeField] private PassthroughCameraFeed _feed;
        [SerializeField] private ArucoDetectorSettings _detectorSettings = new ArucoDetectorSettings();

        [Tooltip("Id of the tag on the robot's chassis. -1 accepts any tag from the dictionary, " +
                 "which is convenient on a bench and a hazard in a room with other tags in it.")]
        [SerializeField] private int _markerId = 0;

        [Tooltip("Side of the printed black square in metres, INCLUDING its black border - the " +
                 "same number marker generators mean by 'marker size'. This scales the whole " +
                 "result: measure the print, do not trust the page setup.")]
        [SerializeField] private float _markerSizeMetres = 0.15f;

        [Header("Where the tag is on the robot")]
        [Tooltip("Height of the tag above the robot's base frame, in metres. The tag is assumed " +
                 "to lie flat on the chassis top, printed face up, centred over the base column.")]
        [SerializeField] private float _tagHeightAboveBase = 0.0f;

        [Tooltip("Horizontal offset from the base frame to the tag centre, in the robot's own " +
                 "frame: x to the robot's right, y along the robot's forward. Leave at zero when " +
                 "the tag really is centred on the column.")]
        [SerializeField] private Vector2 _tagPlanarOffset = Vector2.zero;

        [Tooltip("Rotation about the tag's normal from 'the top of the printed tag' to 'the " +
                 "direction the robot faces', in degrees. Print the tag with its top pointing " +
                 "the way the robot faces and this stays at zero.")]
        [SerializeField] private float _tagYawOffsetDegrees = 0f;

        [Tooltip("How the robot's height is decided. Keep Robot Height takes only horizontal " +
                 "position and yaw from the tag and leaves the height alone - the tag's height " +
                 "on the chassis then never has to be measured. Trim it with the thumbstick " +
                 "before pressing Apply.")]
        [SerializeField] private VerticalMode _verticalMode = VerticalMode.KeepRobotHeight;

        [Tooltip("How fast the thumbstick raises and lowers the proposed pose, in metres per " +
                 "second, while the calibration is waiting to be confirmed. Zero disables it.")]
        [SerializeField] private float _heightNudgeMetresPerSecond = 0.25f;

        [Tooltip("Force the placed robot upright, taking only the tag's yaw. Strongly " +
                 "recommended: the robot is standing on the floor, and a single planar marker " +
                 "estimates tilt far less reliably than it estimates yaw.")]
        [SerializeField] private bool _levelToHorizontal = true;

        [Header("What gets moved")]
        [SerializeField] private ApplyMode _applyMode = ApplyMode.Auto;

        [Tooltip("The robot's placement handle. Needed for an ArticulationBody root, which " +
                 "ignores writes to its Transform and has to be moved with TeleportRoot.")]
        [SerializeField] private PlacementHandle _robotHandle;

        [Tooltip("The robot's TF anchor, if it has one. Read to decide what owns the robot's " +
                 "pose, and to name the frame the robot's base is published as.")]
        [SerializeField] private TfAnchor _robotAnchor;

        [Tooltip("Frame the robot's base is published as. Used when moving the TF origin; " +
                 "defaults to the TF anchor's frame when left empty.")]
        [SerializeField] private string _robotBaseFrame = "base_link";

        [Header("Search")]
        [SerializeField] private float _searchTimeoutSeconds = 10f;

        [Tooltip("Seconds between detection passes. Detection runs off the main thread, but the " +
                 "frame readback does not, so this keeps the headset's frame rate intact.")]
        [SerializeField] private float _detectionIntervalSeconds = 0.15f;

        [Tooltip("Good readings to collect before proposing a pose. More is steadier - the " +
                 "readings are averaged - but takes longer to gather.")]
        [SerializeField] private int _requiredSamples = 8;

        [Tooltip("Fewest readings that will do if the timeout arrives first. Below three, one " +
                 "bad reading is the answer.")]
        [SerializeField] private int _minimumSamples = 3;

        [Tooltip("Reject a reading whose corners reproject further than this. A good read on a " +
                 "sharp tag is well under a pixel.")]
        [SerializeField] private float _maxReprojectionErrorPixels = 2.5f;

        [Tooltip("Reject a reading of a tag smaller than this across the frame - too few pixels " +
                 "per cell for the corners to mean anything.")]
        [SerializeField] private float _minMarkerPixels = 40f;

        [Header("Feedback")]
        [SerializeField] private MarkerPoseGizmo _gizmo;

        private ArucoDetector _detector;
        private Coroutine _search;
        private Task<int> _detection;

        // Detection results are written by the worker and read on the main thread once the task
        // has completed, which is the handover - no lock needed, and none would help if there were
        private readonly List<ArucoDetection> _workerResults = new List<ArucoDetection>(4);
        private CameraFrame _workerFrame;

        private readonly List<Vector3> _samplePositions = new List<Vector3>(16);
        private readonly List<Quaternion> _sampleRotations = new List<Quaternion>(16);

        // What was undone if the user says no
        private Transform _movedTransform;
        private ArticulationBody _movedBody;
        private Vector3 _previousPosition;
        private Quaternion _previousRotation;
        private bool _previousAnchorToTf;
        private bool _anchorWasChanged;

        // Counters for a failure message that names the actual problem
        private int _framesExamined;
        private int _contoursSeen;
        private int _quadsSeen;
        private int _markersSeen;
        private int _markersRejected;

        // Brightness of the most recent frame, kept so a search that finds nothing can say
        // whether it was looking at anything. A feed that opens and ticks but delivers a
        // uniform frame fails identically to a room with no tag in it, and the difference is
        // the difference between a camera bug and a printing one.
        private byte _lastMinGray;
        private byte _lastMaxGray;
        private byte _lastMeanGray;
        private bool _sawUniformFrame;
        private int _mirroredSeen;

        // How far the user has trimmed the proposed height with the thumbstick, in metres.
        // Kept for the status line and reset per search; the undo pose is untouched by it, so
        // Cancel still restores where the robot was before any of this started.
        private float _heightNudge;

        // The proposal sentence without the live trim readout, so the trim can be re-appended
        // as it changes rather than accumulating on the end of itself
        private string _proposalMessage = "";

        public CalibrationState State { get; private set; } = CalibrationState.Idle;

        // One line for the calibration panel, written to be actionable rather than merely true
        public string StatusMessage { get; private set; } = "";

        public bool IsBusy => State == CalibrationState.Searching;

        public bool AwaitingConfirmation => State == CalibrationState.AwaitingConfirmation;

        public float SearchProgress { get; private set; }

        private void Awake()
        {
            if (_feed == null)
                _feed = GetComponent<PassthroughCameraFeed>();
        }

        // Wired to the calibration panel's button.
        public void BeginCalibration()
        {
            if (State == CalibrationState.Searching)
                return;

            // Starting again after a proposal throws the proposal away rather than stacking a
            // second one on top of it
            if (State == CalibrationState.AwaitingConfirmation)
                CancelCalibration();

            if (_feed == null)
            {
                State = CalibrationState.Unavailable;
                StatusMessage = "No passthrough camera feed is wired to the calibrator.";
                return;
            }

            _feed.Open();
            if (_feed.Status == PassthroughCameraFeed.FeedStatus.Unsupported)
            {
                State = CalibrationState.Unavailable;
                StatusMessage = _feed.Detail;
                return;
            }

            _detector = new ArucoDetector(_detectorSettings);
            _search = StartCoroutine(Search());
        }

        // Keep the pose the search proposed.
        public void ConfirmCalibration()
        {
            if (State != CalibrationState.AwaitingConfirmation)
                return;

            State = CalibrationState.Applied;
            StatusMessage = "Calibration applied.";
            _gizmo?.SetVisible(false);
            _feed?.Close();
            ClearUndo();
        }

        // Put everything back exactly as it was, including the TF anchoring mode if that was
        // switched to make the placement stick.
        public void CancelCalibration()
        {
            if (State == CalibrationState.Searching && _search != null)
            {
                StopCoroutine(_search);
                _search = null;
            }

            RestorePrevious();

            _gizmo?.SetVisible(false);
            _feed?.Close();

            if (State != CalibrationState.Unavailable)
            {
                State = CalibrationState.Idle;
                StatusMessage = "";
            }
        }

        // Whatever actually carries the robot's pose. Used to read the robot's current height,
        // which is the point of KeepRobotHeight.
        private Transform RobotTransform()
        {
            return _robotHandle != null ? _robotHandle.Target : null;
        }

        // Thumbstick trim while the proposal is on screen. This is the honest place for a manual
        // correction: the robot is already sitting in the room next to the real one, so the user
        // is adjusting against the thing itself rather than against a number in the Inspector.
        //
        // It moves whatever was moved, not the robot specifically. A vertical shift of the TF
        // origin is a vertical shift of everything hanging off it - the robot, the scan and the
        // cloud together - which is what is wanted when TF owns the pose; shifting the robot
        // alone there would just be undone on the next TF update.
        private void Update()
        {
            if (State != CalibrationState.AwaitingConfirmation || _movedTransform == null)
                return;

            if (_heightNudgeMetresPerSecond <= 0f)
                return;

            float stick = ReadVerticalStick();
            if (Mathf.Approximately(stick, 0f))
                return;

            float delta = stick * _heightNudgeMetresPerSecond * Time.unscaledDeltaTime;
            _heightNudge += delta;

            Vector3 moved = _movedTransform.position + Vector3.up * delta;
            if (_movedBody != null)
                _movedBody.TeleportRoot(moved, _movedTransform.rotation);
            else
                _movedTransform.SetPositionAndRotation(moved, _movedTransform.rotation);

            // Keep the grab sphere on the thing it moves, or the next grab snaps it back
            PlacementHandle handle = FindHandleFor(_movedTransform);
            if (handle != null)
            {
                handle.SetTargetPose(moved, _movedTransform.rotation);
                handle.SnapToTarget();
            }

            StatusMessage = $"{_proposalMessage} (height {_heightNudge * 100f:+0.0;-0.0;0} cm)";
        }

        // Either stick, so it does not matter which hand the user has free. Deadzoned because a
        // resting thumb on a drifting stick would walk the robot away while they look at it.
        private static float ReadVerticalStick()
        {
            const float deadzone = 0.15f;
            float left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick).y;
            float right = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;
            float stick = Mathf.Abs(right) > Mathf.Abs(left) ? right : left;
            return Mathf.Abs(stick) < deadzone ? 0f : stick;
        }

        private void OnDisable()
        {
            if (_search != null)
            {
                StopCoroutine(_search);
                _search = null;
            }
            _feed?.Close();
        }

        private IEnumerator Search()
        {
            State = CalibrationState.Searching;
            StatusMessage = "Look at the tag on the robot...";
            SearchProgress = 0f;

            _samplePositions.Clear();
            _sampleRotations.Clear();
            _framesExamined = 0;
            _contoursSeen = 0;
            _quadsSeen = 0;
            _markersSeen = 0;
            _markersRejected = 0;
            _lastMinGray = 0;
            _lastMaxGray = 0;
            _lastMeanGray = 0;
            _sawUniformFrame = false;
            _mirroredSeen = 0;
            _heightNudge = 0f;

            float deadline = Time.realtimeSinceStartup + _searchTimeoutSeconds;
            float nextPass = 0f;

            while (Time.realtimeSinceStartup < deadline)
            {
                SearchProgress = Mathf.Clamp01(
                    1f - (deadline - Time.realtimeSinceStartup) / Mathf.Max(_searchTimeoutSeconds, 0.001f));

                if (_feed.Status == PassthroughCameraFeed.FeedStatus.PermissionDenied
                    || _feed.Status == PassthroughCameraFeed.FeedStatus.Failed)
                {
                    State = CalibrationState.Unavailable;
                    StatusMessage = _feed.Detail;
                    _search = null;
                    yield break;
                }

                if (Time.realtimeSinceStartup >= nextPass && _detection == null
                    && _feed.TryAcquireFrame(out CameraFrame frame))
                {
                    nextPass = Time.realtimeSinceStartup + _detectionIntervalSeconds;
                    _workerFrame = frame;

                    _lastMinGray = frame.MinGray;
                    _lastMaxGray = frame.MaxGray;
                    _lastMeanGray = frame.MeanGray;
                    if (frame.IsUniform)
                        _sawUniformFrame = true;

                    // Detection is tens of milliseconds on a full-resolution frame. On the main
                    // thread that is a visible hitch every pass, and a hitch in a headset is not
                    // a cosmetic problem.
                    _detection = Task.Run(() =>
                    {
                        var found = _detector.Detect(_workerFrame.Gray, _workerFrame.Width,
                            _workerFrame.Height);
                        _workerResults.Clear();
                        _workerResults.AddRange(found);
                        return _detector.LastQuadCount;
                    });
                }

                if (_detection != null && _detection.IsCompleted)
                {
                    _framesExamined++;
                    if (_detection.IsFaulted)
                    {
                        Debug.LogError($"[XRViz] ArUco detection failed: {_detection.Exception}", this);
                    }
                    else
                    {
                        // Safe to read off the detector here rather than through the task's
                        // result: the task has completed, which is the same handover the worker
                        // results themselves rely on
                        _contoursSeen += _detector.LastCandidateCount;
                        _quadsSeen += _detection.Result;
                        _mirroredSeen += _detector.LastMirroredCount;
                        ConsumeResults();
                    }

                    _detection = null;

                    if (_samplePositions.Count >= _requiredSamples)
                        break;

                    // Once a pass has found nothing, the counters are the only thing that says
                    // where it stopped - and they are far more useful live, where you can move
                    // and watch them respond, than in a single sentence ten seconds later
                    StatusMessage = _samplePositions.Count > 0
                        ? $"Hold still - {_samplePositions.Count}/{_requiredSamples} readings"
                        : $"Looking... {DescribeProgress()}";
                }

                yield return null;
            }

            // Let an in-flight pass finish rather than throwing away a reading that may be the
            // one that gets us over the minimum
            while (_detection != null && !_detection.IsCompleted)
                yield return null;

            if (_detection != null)
            {
                if (!_detection.IsFaulted)
                {
                    _contoursSeen += _detector.LastCandidateCount;
                    _quadsSeen += _detection.Result;
                    _mirroredSeen += _detector.LastMirroredCount;
                    ConsumeResults();
                }
                _detection = null;
            }

            SearchProgress = 1f;
            _search = null;

            if (_samplePositions.Count < _minimumSamples)
            {
                State = CalibrationState.TimedOut;
                StatusMessage = DescribeFailure();
                // Also to the log. The panel says this once and then the user walks away from
                // it; the calibration cannot run anywhere but the headset, so the log is the
                // only place a failure can be read after the fact.
                Debug.LogWarning($"[XRViz] Calibration: {StatusMessage}", this);
                _feed.Close();
                yield break;
            }

            Propose();
        }

        // Turn this pass's detections into world-space tag readings, dropping the ones that are
        // not worth averaging in.
        private void ConsumeResults()
        {
            foreach (var detection in _workerResults)
            {
                if (_markerId >= 0 && detection.Id != _markerId)
                {
                    _markersSeen++;
                    _markersRejected++;
                    continue;
                }

                _markersSeen++;

                if (detection.MinSideLength < _minMarkerPixels)
                {
                    _markersRejected++;
                    continue;
                }

                var pose = MarkerPoseSolver.Solve(detection.Corners, _markerSizeMetres,
                    _workerFrame.Intrinsics);

                if (!pose.Valid || pose.ReprojectionErrorPixels > _maxReprojectionErrorPixels)
                {
                    _markersRejected++;
                    continue;
                }

                // Camera-local to world, using the camera pose captured with the pixels rather
                // than wherever the head has drifted to since
                _samplePositions.Add(_workerFrame.CameraPosition
                    + _workerFrame.CameraRotation * pose.Position);
                _sampleRotations.Add(_workerFrame.CameraRotation * pose.Rotation);
            }

            _workerResults.Clear();
        }

        private void Propose()
        {
            Vector3 tagPosition = MedianPosition(_samplePositions);
            Quaternion tagRotation = AverageRotation(_sampleRotations);

            ComputeRobotPose(tagPosition, tagRotation, out Vector3 basePosition,
                out Quaternion baseRotation);

            _gizmo?.Show(tagPosition, tagRotation, _markerSizeMetres);

            if (!Apply(basePosition, baseRotation, out string how))
            {
                State = CalibrationState.TimedOut;
                StatusMessage = how;
                _feed.Close();
                return;
            }

            float spread = PositionSpread(_samplePositions);

            // What was done about the vertical, and what to do if it is wrong. In
            // KeepRobotHeight the tag's height is not used at all, so warning about it would be
            // noise; in FromTagHeight a zero is not a plausible measurement but an unset field -
            // the tag lies on the chassis TOP, so it is always some way above the base - and
            // left at zero the base is placed exactly at the tag, floating the robot by however
            // high the tag really is. That looks like a broken calibration rather than an
            // unfilled box, so say which it is.
            string note;
            if (_verticalMode == VerticalMode.KeepRobotHeight)
                note = _heightNudgeMetresPerSecond > 0f
                    ? " Height kept as it was - thumbstick up/down to trim it."
                    : " Height kept as it was.";
            else if (Mathf.Approximately(_tagHeightAboveBase, 0f))
                note = " NOTE: Tag Height Above Base is 0, so the base was placed AT the tag - " +
                       "the robot will float by the tag's real height. Measure base to tag and " +
                       "set it, or switch Vertical Mode to Keep Robot Height.";
            else
                note = "";

            State = CalibrationState.AwaitingConfirmation;
            _proposalMessage = $"Tag {(_markerId >= 0 ? _markerId.ToString() : "found")} from " +
                               $"{_samplePositions.Count} readings (spread {spread * 1000f:F0} mm). " +
                               $"{how}{note} Does the robot line up? Apply or Cancel.";
            StatusMessage = _proposalMessage;

            Debug.Log($"[XRViz] Calibration: tag at {tagPosition} -> base {basePosition}, " +
                      $"{_samplePositions.Count} readings, spread {spread * 1000f:F0} mm, " +
                      $"tag height above base {_tagHeightAboveBase:F3} m. {how}{note}", this);
        }

        // The robot's base pose implied by a tag lying flat on the chassis, face up.
        //
        // Two physical directions come out of the tag and everything else follows from them: the
        // tag's normal (out of its printed face) is the robot's up, and the direction the top of
        // the print points is the robot's forward, turned by the yaw offset.
        private void ComputeRobotPose(Vector3 tagPosition, Quaternion tagRotation,
            out Vector3 basePosition, out Quaternion baseRotation)
        {
            Vector3 up = tagRotation * Vector3.forward;
            Vector3 forward = tagRotation * Vector3.up;

            if (_levelToHorizontal)
                up = Vector3.up;

            forward = Vector3.ProjectOnPlane(forward, up);
            if (forward.sqrMagnitude < 1e-8f)
            {
                // The tag is being viewed edge-on enough that its print-up direction has no
                // component in the robot's ground plane; any forward is as good as any other
                forward = Vector3.ProjectOnPlane(tagRotation * Vector3.right, up);
                if (forward.sqrMagnitude < 1e-8f)
                    forward = Vector3.forward;
            }

            forward = Quaternion.AngleAxis(_tagYawOffsetDegrees, up) * forward.normalized;
            baseRotation = Quaternion.LookRotation(forward, up);

            // The tag sits above the base and possibly off to one side, both measured in the
            // robot's own frame - so the base is the tag less that offset
            Vector3 offset = new Vector3(_tagPlanarOffset.x, _tagHeightAboveBase, _tagPlanarOffset.y);
            basePosition = tagPosition - baseRotation * offset;

            // Height from the robot rather than from the tag, when asked. The tag pins the
            // horizontal position and the yaw, which is what it is good at; the vertical it is
            // worst at, and it is also the axis a wrong tag height, a wrong marker size or a
            // wrong lens offset all come out along. Leaving the robot at the height it already
            // stands at is both more accurate and one fewer number to measure.
            if (_verticalMode == VerticalMode.KeepRobotHeight)
            {
                Transform robot = RobotTransform();
                if (robot != null)
                    basePosition.y = robot.position.y;
            }

            basePosition.y += _heightNudge;
        }

        private bool Apply(Vector3 basePosition, Quaternion baseRotation, out string how)
        {
            how = "";
            ClearUndo();

            bool anchoredToTf = _robotAnchor != null && _robotAnchor.AnchorToTf;
            ApplyMode mode = _applyMode;
            if (mode == ApplyMode.Auto)
                mode = anchoredToTf ? ApplyMode.MoveTfOrigin : ApplyMode.MoveRobot;

            if (mode == ApplyMode.MoveTfOrigin)
                return ApplyToTfOrigin(basePosition, baseRotation, out how);

            // Moving the robot itself while TF owns its pose would be undone on the next frame,
            // so hand the robot back to manual placement and say so rather than appearing to
            // work and then silently snapping back
            if (anchoredToTf)
            {
                _previousAnchorToTf = true;
                _anchorWasChanged = true;
                _robotAnchor.SetAnchorToTf(false);
                how = "Switched the robot to hand placement so the result would stick.";
            }

            if (_robotHandle != null)
            {
                Transform target = _robotHandle.Target;
                if (target == null)
                {
                    how = "The robot's placement handle has no target assigned.";
                    return false;
                }

                RememberTransform(target);
                _robotHandle.SetTargetPose(basePosition, baseRotation);
                _robotHandle.SnapToTarget();
                how += " Moved the robot.";
                return true;
            }

            how = "No placement handle is wired to the calibrator, so there is nothing to move.";
            return false;
        }

        // Anchor the whole TF world instead of the robot alone.
        //
        // When the robot's pose comes from TF, moving the robot is pointless - TF puts it back
        // next frame. What has to move is the TF origin, which is the one thing in the scene that
        // stands for "where the robot's fixed frame is in this room" (see RosTfTree). Doing it
        // this way also drags the laser scan and the point cloud along, all still consistent with
        // each other, which is the entire reason TF placement exists.
        private bool ApplyToTfOrigin(Vector3 basePosition, Quaternion baseRotation, out string how)
        {
            how = "";

            var tree = RosTfTree.Instance;
            if (tree == null)
            {
                how = "TF placement is on but there is no TF Origin in the scene to move.";
                return false;
            }

            string frame = !string.IsNullOrEmpty(_robotBaseFrame)
                ? _robotBaseFrame
                : (_robotAnchor != null ? _robotAnchor.FrameId : null);

            if (string.IsNullOrEmpty(frame))
            {
                how = "No robot base frame is configured, so the TF origin cannot be solved for.";
                return false;
            }

            // Where the base sits relative to the fixed frame, according to TF
            if (!tree.TryGetPose(frame, out Vector3 localPosition, out Quaternion localRotation))
            {
                how = $"'{frame}' is not in /tf yet, so the TF origin cannot be placed from it.";
                return false;
            }

            // We want origin * local = target, so origin = target * local^-1
            Quaternion originRotation = baseRotation * Quaternion.Inverse(localRotation);
            Vector3 originPosition = basePosition - originRotation * localPosition;

            RememberTransform(tree.transform);

            // Go through the TF origin's own handle where it has one, so the grab sphere re-homes
            // onto the new pose instead of yanking everything back the first time it is touched
            PlacementHandle handle = FindHandleFor(tree.transform);
            if (handle != null)
            {
                handle.SetTargetPose(originPosition, originRotation);
                handle.SnapToTarget();
            }
            else
            {
                tree.transform.SetPositionAndRotation(originPosition, originRotation);
            }

            how = "Moved the TF origin, so the scan and cloud follow the robot.";
            return true;
        }

        private static PlacementHandle FindHandleFor(Transform target)
        {
            var handles = FindObjectsByType<PlacementHandle>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var handle in handles)
            {
                if (handle.Target == target)
                    return handle;
            }
            return null;
        }

        private void RememberTransform(Transform target)
        {
            _movedTransform = target;
            _movedBody = target.GetComponent<ArticulationBody>();
            _previousPosition = target.position;
            _previousRotation = target.rotation;
        }

        private void RestorePrevious()
        {
            if (_movedTransform != null)
            {
                if (_movedBody != null)
                    _movedBody.TeleportRoot(_previousPosition, _previousRotation);
                else
                    _movedTransform.SetPositionAndRotation(_previousPosition, _previousRotation);

                PlacementHandle handle = FindHandleFor(_movedTransform);
                handle?.SnapToTarget();
            }

            if (_anchorWasChanged && _robotAnchor != null)
                _robotAnchor.SetAnchorToTf(_previousAnchorToTf);

            ClearUndo();
        }

        private void ClearUndo()
        {
            _movedTransform = null;
            _movedBody = null;
            _anchorWasChanged = false;
        }

        // Name what actually went wrong. "No tag found" is true of a covered lens, a tag too far
        // away, the wrong dictionary and the wrong id alike, and each has a different fix.
        private string DescribeFailure()
        {
            if (_framesExamined == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - no camera frames " +
                       $"arrived: {_feed.AcquireIssue}. ({_feed.Detail})";

            // A uniform frame cannot yield a contour whatever is in front of the camera, so
            // say that instead of sending the user off to relight a tag that was never seen
            if (_sawUniformFrame || _contoursSeen == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - the camera delivered " +
                       $"{_framesExamined} frame(s) but nothing dark was found in any of them " +
                       $"({DescribeFrameBrightness()}). That is a broken feed rather than an " +
                       "unreadable tag: a real room always has dark edges in it.";

            if (_quadsSeen == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - {_contoursSeen} dark " +
                       "outline(s) were traced but none was a convex quad. Move closer, and " +
                       "check the tag is flat, lit, and not at a glancing angle.";

            // Checked before the generic "wrong dictionary" message below, because it is the
            // same symptom with a completely different cause and the wrong advice is expensive:
            // it sends the user off to reprint a tag that was never the problem.
            if (_markersSeen == 0 && _mirroredSeen > 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - {_mirroredSeen} tag " +
                       "reading(s) decoded only when MIRRORED, so the camera image is being " +
                       "flipped the wrong way round. The tag and the dictionary are fine; " +
                       "invert Vertical Flip on the PassthroughCameraFeed.";

            if (_markersSeen == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - square shapes were found " +
                       $"but none decoded as a {_detectorSettings.Dictionary} tag. Check the " +
                       "printed tag came from that dictionary.";

            if (_markersRejected > 0 && _samplePositions.Count == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - saw {_markersSeen} tag " +
                       $"reading(s) but rejected all of them. " +
                       (_markerId >= 0 ? $"Expected id {_markerId}. " : "") +
                       "Get closer so the tag fills more of the view.";

            return $"Timed out after {_searchTimeoutSeconds:F0} s with only " +
                   $"{_samplePositions.Count} good reading(s); {_minimumSamples} are needed.";
        }

        private string DescribeProgress()
        {
            // The mirrored count only appears once it is non-zero: it is meaningless noise on a
            // correctly oriented feed, and unmissable on a flipped one.
            string mirrored = _mirroredSeen > 0 ? $", {_mirroredSeen} MIRRORED" : "";
            return $"{_framesExamined} frames, {_contoursSeen} outlines, {_quadsSeen} quads, " +
                   $"{_markersSeen} tags{mirrored} ({DescribeFrameBrightness()})";
        }

        private string DescribeFrameBrightness()
        {
            if (_framesExamined == 0)
                return "no frames yet";
            return $"grey {_lastMinGray}-{_lastMaxGray}, mean {_lastMeanGray}";
        }

        // Component-wise median rather than a mean: one bad reading from a half-occluded tag
        // pulls a mean of eight by centimetres and a median not at all.
        private static Vector3 MedianPosition(List<Vector3> positions)
        {
            int n = positions.Count;
            var xs = new float[n];
            var ys = new float[n];
            var zs = new float[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = positions[i].x;
                ys[i] = positions[i].y;
                zs[i] = positions[i].z;
            }
            System.Array.Sort(xs);
            System.Array.Sort(ys);
            System.Array.Sort(zs);
            return new Vector3(xs[n / 2], ys[n / 2], zs[n / 2]);
        }

        private static float PositionSpread(List<Vector3> positions)
        {
            Vector3 median = MedianPosition(positions);
            float worst = 0f;
            foreach (var position in positions)
                worst = Mathf.Max(worst, (position - median).magnitude);
            return worst;
        }

        // Quaternion averaging by summing components, with signs aligned to the first sample.
        // Exact averaging needs an eigen-decomposition; over readings that agree to a few degrees
        // - which the reprojection filter has already guaranteed - the difference does not reach
        // the third decimal place.
        private static Quaternion AverageRotation(List<Quaternion> rotations)
        {
            Quaternion reference = rotations[0];
            float x = 0f, y = 0f, z = 0f, w = 0f;

            foreach (var rotation in rotations)
            {
                Quaternion q = rotation;
                // q and -q are the same rotation; summing them raw would cancel
                if (Quaternion.Dot(reference, q) < 0f)
                    q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

                x += q.x;
                y += q.y;
                z += q.z;
                w += q.w;
            }

            float magnitude = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (magnitude < 1e-6f)
                return reference;

            return new Quaternion(x / magnitude, y / magnitude, z / magnitude, w / magnitude);
        }
    }
}
