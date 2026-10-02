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

            // Place the origin below the tag by Tag Height Above Origin. Correct if that
            // number is measured; everything floating by the tag's height if left at zero.
            FromTagHeight,
        }

        public enum ApplyMode
        {
            // Move the TF origin wherever there is one, and the robot only in a scene with no
            // TF world to place. The tag is a measurement of where the fixed frame is in this
            // room, so that is what it should write to - whether or not the robot happens to be
            // anchored to TF at the time. A robot that is anchored then follows through /tf; one
            // that is not stays where the user put it, which is the correct answer and not a
            // failure: the calibration never drops the robot onto the tag.
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

        // A size per marker id, for rigs that carry more than one tag - a big one on the chassis
        // and a small one on the gripper, say. Size is the one thing about a marker that cannot
        // be read out of the image: the dictionary fixes the id and the bit pattern, never the
        // physical size, and getting it wrong scales the distance by exactly the same factor.
        [System.Serializable]
        public struct MarkerSize
        {
            public int Id;
            public float SizeMetres;
        }

        [Tooltip("Physical sizes for individual marker ids, when they are not all the same size. " +
                 "Any id not listed uses Marker Size Metres.")]
        [SerializeField] private MarkerSize[] _markerSizes = new MarkerSize[0];

        [Tooltip("Report every dictionary marker seen, not just the one being calibrated " +
                 "against. Costs a pose solve per extra marker per pass, which is nothing.")]
        [SerializeField] private bool _reportAllMarkers = true;

        // Both of these are measured from the frame the calibration PLACES, which is the TF
        // origin whenever TF owns the robot's pose (the normal case) and the robot's own root
        // only when the robot is placed by hand. The tag is the one measurement of where the
        // fixed frame is in this room; every other frame - base_link included - is then
        // populated from /tf underneath it, so nothing here is measured from base_link.
        [Header("Where the tag is, relative to what gets placed")]
        [Tooltip("Height of the tag above the placed frame - the TF origin when TF owns the " +
                 "robot, the robot's own root otherwise - in metres. Unused in Keep Robot " +
                 "Height, which is the default.")]
        [UnityEngine.Serialization.FormerlySerializedAs("_tagHeightAboveBase")]
        [SerializeField] private float _tagHeightAboveOrigin = 0.0f;

        [Tooltip("Horizontal offset from the placed frame to the tag centre, in that frame's " +
                 "own axes: x to its right, y along its forward. Leave at zero when the tag " +
                 "really is over the origin.")]
        [SerializeField] private Vector2 _tagPlanarOffset = Vector2.zero;

        [Tooltip("Rotation about the tag's normal from 'the top of the printed tag' to the " +
                 "forward of the placed frame (ROS +X of the fixed frame, when the TF origin is " +
                 "what moves), in degrees. Print the tag with its top pointing the way the " +
                 "robot faces and this stays at zero.")]
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
                 "pose: anchored to TF, the TF origin is what the calibration moves.")]
        [SerializeField] private TfAnchor _robotAnchor;

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

        [Tooltip("Reject readings taken while the head is turning faster than this, in degrees " +
                 "per second. The camera pose is sampled when the frame is read, but the frame " +
                 "is about 40 ms old, so head motion biases the reading in the direction of " +
                 "travel - and being a bias rather than noise, averaging will not remove it. " +
                 "Zero disables the gate.")]
        [SerializeField] private float _maxHeadAngularSpeed = 25f;

        [Tooltip("Reject readings taken while the head is moving faster than this, in metres " +
                 "per second. Same reason as the angular limit. Zero disables the gate.")]
        [SerializeField] private float _maxHeadLinearSpeed = 0.25f;

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
        private int _rejectedForMotion;

        // Every dictionary marker seen during the search, not only the one being calibrated
        // against: id -> how often, how big on screen, how far away, how cleanly it read
        private readonly Dictionary<int, ObservedMarker> _observed = new Dictionary<int, ObservedMarker>();

        private struct ObservedMarker
        {
            public int Count;
            public float MinSidePixels;
            public float DistanceMetres;
            public float SizeMetres;
            public int BitErrors;
            public float Contrast;
        }

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
            _rejectedForMotion = 0;
            _observed.Clear();
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
        private float SizeForMarker(int id)
        {
            if (_markerSizes != null)
            {
                foreach (var entry in _markerSizes)
                {
                    if (entry.Id == id && entry.SizeMetres > 0f)
                        return entry.SizeMetres;
                }
            }
            return _markerSizeMetres;
        }

        private bool IsHeadMoving()
        {
            return (_maxHeadAngularSpeed > 0f
                    && _workerFrame.HeadAngularSpeed > _maxHeadAngularSpeed)
                || (_maxHeadLinearSpeed > 0f
                    && _workerFrame.HeadLinearSpeed > _maxHeadLinearSpeed);
        }

        private void Observe(ArucoDetection detection, MarkerPose pose, float size)
        {
            _observed.TryGetValue(detection.Id, out ObservedMarker seen);
            seen.Count++;
            seen.MinSidePixels = detection.MinSideLength;
            seen.SizeMetres = size;
            seen.BitErrors = detection.BitErrors;
            seen.Contrast = detection.Contrast;
            seen.DistanceMetres = pose.Valid ? pose.Position.magnitude : 0f;
            _observed[detection.Id] = seen;
        }

        // Every dictionary marker the search saw, newest reading per id. The target is starred,
        // so a room with several tags in it reads as a list rather than a puzzle.
        public string DescribeObservedMarkers()
        {
            if (_observed.Count == 0)
                return "no markers seen";

            var ids = new List<int>(_observed.Keys);
            ids.Sort();

            var parts = new List<string>(ids.Count);
            foreach (int id in ids)
            {
                ObservedMarker m = _observed[id];
                string distance = m.DistanceMetres > 0f ? $"{m.DistanceMetres:F2} m" : "no pose";
                parts.Add($"{(id == _markerId ? "*" : "")}id {id}: {m.Count}x, " +
                          $"{m.MinSidePixels:F0} px, {distance} @ {m.SizeMetres * 1000f:F0} mm, " +
                          $"{m.BitErrors} bit err, contrast {m.Contrast:F2}");
            }
            return string.Join("; ", parts);
        }

        private void ConsumeResults()
        {
            foreach (var detection in _workerResults)
            {
                // Solve every marker, whichever id it is. The pose is what turns an apparent
                // size in pixels into a distance, and knowing that a second tag is 2.3 m away is
                // exactly what makes a list of ids useful rather than trivia.
                float size = SizeForMarker(detection.Id);
                var pose = MarkerPoseSolver.Solve(detection.Corners, size,
                    _workerFrame.Intrinsics);

                if (_reportAllMarkers)
                    Observe(detection, pose, size);

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

                // Taken while the head was moving, so the 40 ms old frame is paired with a pose
                // from 40 ms too late. Discarding is the whole defence: the error is a bias in
                // the direction of travel, so keeping these and letting the median sort it out
                // does not work - the median of eight consistently biased readings is biased.
                if (IsHeadMoving())
                {
                    _markersRejected++;
                    _rejectedForMotion++;
                    continue;
                }

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

            ComputePlacedPose(tagPosition, tagRotation, out Vector3 placedPosition,
                out Quaternion placedRotation);

            _gizmo?.Show(tagPosition, tagRotation, _markerSizeMetres);

            if (!Apply(placedPosition, placedRotation, out string how))
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
            // the tag lies on the chassis TOP, so it is always some way above the fixed frame -
            // and left at zero the fixed frame is placed exactly at the tag, lifting the whole
            // TF world by however high the tag really is. That looks like a broken calibration
            // rather than an unfilled box, so say which it is.
            string note;
            if (_verticalMode == VerticalMode.KeepRobotHeight)
                note = _heightNudgeMetresPerSecond > 0f
                    ? " Height kept as it was - thumbstick up/down to trim it."
                    : " Height kept as it was.";
            else if (Mathf.Approximately(_tagHeightAboveOrigin, 0f))
                note = " NOTE: Tag Height Above Origin is 0, so the origin was placed AT the " +
                       "tag - everything under it will float by the tag's real height. Measure " +
                       "origin to tag and set it, or switch Vertical Mode to Keep Robot Height.";
            else
                note = "";

            State = CalibrationState.AwaitingConfirmation;
            // Ask about the thing that actually moved. On the TF path with the robot not
            // anchored, "does the robot line up?" has no bearing on whether the calibration was
            // right - the origin indicator is what to look at.
            string check = WillMoveTfOrigin()
                ? "Is the TF origin where the tag is?"
                : "Does the robot line up?";
            _proposalMessage = $"Tag {(_markerId >= 0 ? _markerId.ToString() : "found")} from " +
                               $"{_samplePositions.Count} readings (spread {spread * 1000f:F0} mm). " +
                               $"{how}{note} {check} Apply or Cancel.";
            StatusMessage = _proposalMessage;

            // placedPosition is what the TAG implies, height included. The height actually
            // used is decided by the apply path (KeepRobotHeight leaves the moved thing's height
            // alone), so do not read this line as the pose that landed.
            Debug.Log($"[XRViz] Calibration: tag at {tagPosition} -> " +
                      $"{(WillMoveTfOrigin() ? "TF origin" : "robot")} from tag {placedPosition}, " +
                      $"{_samplePositions.Count} readings, spread {spread * 1000f:F0} mm, " +
                      $"{_rejectedForMotion} dropped for head motion, " +
                      $"tag height above origin {_tagHeightAboveOrigin:F3} m. {how}{note}\n" +
                      $"  markers seen: {DescribeObservedMarkers()}", this);
        }

        // The pose of the frame being placed, implied by a tag lying flat on the chassis, face
        // up. That frame is the TF origin on the normal path and the robot's own root when the
        // robot is placed by hand - the tag offsets below are read in whichever it is.
        //
        // Two physical directions come out of the tag and everything else follows from them: the
        // tag's normal (out of its printed face) is the frame's up, and the direction the top of
        // the print points is its forward, turned by the yaw offset.
        private void ComputePlacedPose(Vector3 tagPosition, Quaternion tagRotation,
            out Vector3 placedPosition, out Quaternion placedRotation)
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
            placedRotation = Quaternion.LookRotation(forward, up);

            // The tag sits above the placed frame's origin and possibly off to one side, both
            // measured in that frame - so the frame is the tag less that offset
            Vector3 offset = new Vector3(_tagPlanarOffset.x, _tagHeightAboveOrigin, _tagPlanarOffset.y);
            placedPosition = tagPosition - placedRotation * offset;

            // The vertical is deliberately NOT decided here. This gives the pose the tag implies;
            // KeepRobotHeight is applied by whoever actually does the moving, because "keep the
            // height" means "keep the height of the thing being moved" and only they know what
            // that is. Doing it here meant reading the robot's Transform even when the robot is
            // not what moves.
        }

        // Which of the two things the tag pose is a measurement of. The tag offsets are read in
        // the frame this names, so the decision is made in one place rather than inline.
        //
        // Auto goes on whether there is a TF world at all, NOT on whether the robot is currently
        // anchored to it. Keying it off the anchor meant the common state - anchor off, because
        // it starts off and is turned on from the TF page - sent the whole calibration down the
        // MoveRobot path and teleported the robot onto the tag, which is the one thing the tag
        // is not a measurement of.
        private bool WillMoveTfOrigin()
        {
            ApplyMode mode = _applyMode;
            if (mode == ApplyMode.Auto)
                mode = RosTfTree.Instance != null ? ApplyMode.MoveTfOrigin : ApplyMode.MoveRobot;
            return mode == ApplyMode.MoveTfOrigin;
        }

        private bool Apply(Vector3 placedPosition, Quaternion placedRotation, out string how)
        {
            how = "";
            ClearUndo();

            bool anchoredToTf = _robotAnchor != null && _robotAnchor.AnchorToTf;

            if (WillMoveTfOrigin())
                return ApplyToTfOrigin(placedPosition, placedRotation, out how);

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

                // KeepRobotHeight, on the path that moves the robot: leave the robot's height
                // alone. Here placedPosition IS this Transform's pose, so reading it is right.
                if (_verticalMode == VerticalMode.KeepRobotHeight)
                    placedPosition.y = target.position.y;
                placedPosition.y += _heightNudge;

                RememberTransform(target);
                _robotHandle.SetTargetPose(placedPosition, placedRotation);
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
        //
        // The tag measures the TF ORIGIN directly - it is not a measurement of base_link that is
        // then walked back up the tree. Composing through base_link's /tf pose is what put the
        // origin a robot-height out: the tag offsets are measured from the frame being placed,
        // so subtracting base_link's offset as well counted the robot's own height twice. Every
        // other frame, base_link included, is populated from /tf once the origin is where the
        // tag says it is - which is also why this no longer needs /tf to have arrived at all.
        private bool ApplyToTfOrigin(Vector3 originPosition, Quaternion originRotation,
            out string how)
        {
            how = "";

            var tree = RosTfTree.Instance;
            if (tree == null)
            {
                how = "TF placement is on but there is no TF Origin in the scene to move.";
                return false;
            }

            // KeepRobotHeight, on the path that moves the fixed frame: leave the fixed frame's
            // height alone. Nothing about the robot is read - the calibration moves tf_origin and
            // the base follows from /tf, so the robot's own Transform is an output here, not an
            // input. The thumbstick trim is the only thing that shifts it.
            if (_verticalMode == VerticalMode.KeepRobotHeight)
                originPosition.y = tree.transform.position.y;
            originPosition.y += _heightNudge;

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

            // Tell the origin's own indicator who moved it. Without this the label reports the
            // move as a hand placement, which is the one thing it is there to distinguish -
            // "did the calibration actually do anything" is asked at exactly this moment.
            TfOriginIndicator.NotifyMoved(tree.transform, "ArUco calibration");

            // Say whether the robot is going to move, because "nothing visibly happened" is
            // otherwise indistinguishable from a failed calibration. With the anchor off the
            // robot staying put IS the correct result - only what hangs off /tf moves.
            bool anchoredToTf = _robotAnchor != null && _robotAnchor.AnchorToTf;
            how = anchoredToTf
                ? "Moved the TF origin, so the robot, scan and cloud all follow it."
                : "Moved the TF origin. The robot is not anchored to TF, so it stays where it " +
                  "is - turn its TF anchor on for it to follow.";
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

            // Before the generic "rejected all of them", because the fix is completely
            // different: hold still rather than get closer.
            if (_rejectedForMotion > 0 && _rejectedForMotion >= _markersRejected / 2
                && _samplePositions.Count == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - saw the tag, but " +
                       $"{_rejectedForMotion} reading(s) were taken while the head was moving " +
                       "and had to be dropped. The camera frame is about 40 ms old, so moving " +
                       "while it is read biases the result. Hold still and look at the tag. " +
                       $"Markers seen: {DescribeObservedMarkers()}";

            if (_markersRejected > 0 && _samplePositions.Count == 0)
                return $"Timed out after {_searchTimeoutSeconds:F0} s - saw {_markersSeen} tag " +
                       $"reading(s) but rejected all of them. " +
                       (_markerId >= 0 ? $"Expected id {_markerId}. " : "") +
                       "Get closer so the tag fills more of the view. " +
                       $"Markers seen: {DescribeObservedMarkers()}";

            return $"Timed out after {_searchTimeoutSeconds:F0} s with only " +
                   $"{_samplePositions.Count} good reading(s); {_minimumSamples} are needed.";
        }

        private string DescribeProgress()
        {
            // The mirrored count only appears once it is non-zero: it is meaningless noise on a
            // correctly oriented feed, and unmissable on a flipped one.
            string mirrored = _mirroredSeen > 0 ? $", {_mirroredSeen} MIRRORED" : "";

            // Head motion is shown only while it is actually costing readings. "Hold still" is
            // the one instruction the user can act on immediately, so it needs to be visible the
            // moment it starts mattering and invisible the rest of the time.
            string motion = _rejectedForMotion > 0
                ? $" - HOLD STILL, {_rejectedForMotion} dropped for motion"
                : "";

            string markers = _observed.Count > 0 ? $"\n{DescribeObservedMarkers()}" : "";

            return $"{_framesExamined} frames, {_contoursSeen} outlines, {_quadsSeen} quads, " +
                   $"{_markersSeen} tags{mirrored} ({DescribeFrameBrightness()}){motion}{markers}";
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
