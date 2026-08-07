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

        private void Awake()
        {
            _defaultPosition = transform.position;
            _defaultRotation = transform.rotation;
            _lastPosition = _defaultPosition;
            _lastRotation = _defaultRotation;
        }

        private void LateUpdate()
        {
            if (_target == null && _targetArticulationBody == null)
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
            ApplyToTarget();
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
