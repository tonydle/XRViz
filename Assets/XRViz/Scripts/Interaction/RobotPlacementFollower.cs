using UnityEngine;

namespace Unity.Robotics
{
    // Grab handle that repositions a robot. Whenever this transform moves (e.g. grabbed
    // via a Meta Interaction SDK Grabbable), the robot's ArticulationBody root is
    // teleported to follow. Only yaw is applied so the robot always stays upright.
    public class RobotPlacementFollower : MonoBehaviour
    {
        [SerializeField] private ArticulationBody _robotRoot;
        // Robot base position relative to the handle, expressed in the handle's yaw space
        [SerializeField] private Vector3 _robotOffset = new Vector3(0f, 0.1f, 0f);

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;

        private void Start()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
        }

        private void LateUpdate()
        {
            if (_robotRoot == null)
                return;

            bool moved = (transform.position - _lastPosition).sqrMagnitude > 1e-8f
                || Quaternion.Angle(transform.rotation, _lastRotation) > 0.05f;
            if (!moved)
                return;

            _lastPosition = transform.position;
            _lastRotation = transform.rotation;

            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            _robotRoot.TeleportRoot(transform.position + yaw * _robotOffset, yaw);
        }
    }
}
