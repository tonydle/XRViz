using UnityEngine;

namespace Unity.Robotics
{
    // Generic grab handle: whenever this transform moves - grabbed via a Meta Interaction SDK
    // Grabbable, by near grab or ray grab - whatever it points at follows.
    //
    // Replaces RobotPlacementFollower and PanelPlacementFollower, which were the same script
    // twice. Their one real difference is preserved here: an ArticulationBody root ignores
    // writes to its Transform and has to be moved with TeleportRoot instead, so assign
    // _targetArticulationBody for robots and _target for everything else.
    public class PlacementHandle : MonoBehaviour
    {
        [SerializeField] private Transform _target;

        // Robots only. Takes priority over _target when both are set.
        [SerializeField] private ArticulationBody _targetArticulationBody;

        // Target pose relative to the handle, expressed in the handle's yaw space
        [SerializeField] private Vector3 _offset = new Vector3(0f, 0.1f, 0f);

        // Keep the target upright, following only the handle's yaw. Off means the target takes
        // the handle's full rotation - wanted for a sensor you want to aim, not for a robot
        // that should stay standing on the floor.
        [SerializeField] private bool _yawOnly = true;

        // Where the handle started. Captured at Awake rather than serialized, so a handle nudged
        // in the Editor before pressing Play resets to where you left it, not to whatever the
        // scene generator last wrote.
        private Vector3 _defaultPosition;
        private Quaternion _defaultRotation;

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;

        // Set while something else owns the target's pose - today that's TfAnchor, which places
        // the target from the TF tree instead. The handle stops writing (and is normally hidden
        // with it) so the two don't fight over the same Transform every frame.
        private bool _suspended;

        // What this handle actually moves, whichever of the two target fields is set
        public Transform Target =>
            _targetArticulationBody != null ? _targetArticulationBody.transform : _target;

        public bool Suspended => _suspended;

        private void Awake()
        {
            _defaultPosition = transform.position;
            _defaultRotation = transform.rotation;
            _lastPosition = _defaultPosition;
            _lastRotation = _defaultRotation;
        }

        private void LateUpdate()
        {
            if (_suspended || (_target == null && _targetArticulationBody == null))
                return;

            // Only write when the handle actually moved, so nothing fights us for control of
            // the target the rest of the time
            bool moved = (transform.position - _lastPosition).sqrMagnitude > 1e-8f
                || Quaternion.Angle(transform.rotation, _lastRotation) > 0.05f;
            if (!moved)
                return;

            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            ApplyToTarget();
        }

        // Put the handle, and whatever it carries, back where the scene put it. Driven by the
        // ROS control panel's "Reset Anchors" button when something has been dragged somewhere
        // unreachable - or off behind you, which is easy to do with ray grab.
        public void ResetToDefault()
        {
            transform.SetPositionAndRotation(_defaultPosition, _defaultRotation);

            // LateUpdate's moved-check would swallow this write, since _last* is now back in
            // line with the handle - so push it through explicitly instead
            _lastPosition = _defaultPosition;
            _lastRotation = _defaultRotation;

            // A suspended handle's target belongs to TF; move the handle home anyway (so it is
            // where you left it when manual placement resumes) but don't drag the target with it
            if (!_suspended)
                ApplyToTarget();
        }

        // Hand the target over to something else, or take it back. Hiding is the default because
        // a grabbable sphere that visibly does nothing when you pull it reads as a broken app;
        // deactivating also unregisters its Interaction SDK interactables for free.
        public void SetSuspended(bool suspended, bool hide = true)
        {
            if (_suspended == suspended)
                return;

            _suspended = suspended;

            if (!suspended)
            {
                // Re-home onto wherever the target ended up before re-enabling, or the first
                // LateUpdate would see a stale handle pose and yank the target back to it
                SnapToTarget();
            }

            if (hide)
                gameObject.SetActive(!suspended);
        }

        // Point this handle at something else, and treat where it ends up as its home.
        //
        // For a handle cloned alongside the visualisation it grips: Instantiate remaps references
        // that point INSIDE the copied hierarchy, but a handle is a separate root object holding
        // an outside reference, so the copy comes out still driving the original - grab the new
        // sphere and the old cloud moves. Re-homing the default pose as well is what makes Reset
        // Layout put the copy back beside its own visualisation rather than on top of the one it
        // was cloned from.
        public void SetTarget(Transform target, ArticulationBody articulationBody = null)
        {
            _target = target;
            _targetArticulationBody = articulationBody;

            SnapToTarget();
            _defaultPosition = transform.position;
            _defaultRotation = transform.rotation;
        }

        // Move the grip relative to whatever it carries. For a visualisation whose size isn't
        // known until the first message arrives - ImageWindow, whose window is sized from the
        // image's own aspect - a serialized offset cannot be right: the handle has to stay clear
        // of an edge that moves. Re-homes rather than writing, because it is the handle that is
        // now in the wrong place, not the target.
        public void SetOffset(Vector3 offset)
        {
            if ((_offset - offset).sqrMagnitude < 1e-10f)
                return;

            _offset = offset;
            SnapToTarget();
        }

        // Place the target directly, ignoring the handle's own pose and offset. For a driver that
        // knows the pose it wants (TfAnchor) rather than one moving a grip about.
        public void SetTargetPose(Vector3 position, Quaternion rotation)
        {
            if (_targetArticulationBody != null)
                _targetArticulationBody.TeleportRoot(position, rotation);
            else if (_target != null)
                _target.SetPositionAndRotation(position, rotation);
        }

        // Move the handle to where it would have to be for ApplyToTarget() to reproduce the
        // target's current pose - the inverse of ApplyToTarget, so control changes hands without
        // anything visibly jumping.
        public void SnapToTarget()
        {
            Transform target = Target;
            if (target == null)
                return;

            Quaternion rotation = target.rotation;
            Quaternion applied = _yawOnly
                ? Quaternion.Euler(0f, rotation.eulerAngles.y, 0f)
                : rotation;

            transform.SetPositionAndRotation(target.position - applied * _offset, rotation);
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
        }

        private void ApplyToTarget()
        {
            Quaternion rotation = _yawOnly
                ? Quaternion.Euler(0f, transform.eulerAngles.y, 0f)
                : transform.rotation;
            Vector3 position = transform.position + rotation * _offset;

            if (_targetArticulationBody != null)
                _targetArticulationBody.TeleportRoot(position, rotation);
            else if (_target != null)
                _target.SetPositionAndRotation(position, rotation);
        }
    }
}
