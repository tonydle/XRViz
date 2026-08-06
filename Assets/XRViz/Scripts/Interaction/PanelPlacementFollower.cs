using UnityEngine;

namespace Unity.Robotics
{
    // Grab handle that repositions a UI panel. Mirrors RobotPlacementFollower, but moves a
    // plain Transform instead of an ArticulationBody root since a Canvas has no physics body.
    public class PanelPlacementFollower : MonoBehaviour
    {
        [SerializeField] private Transform _panel;
        [SerializeField] private Vector3 _panelOffset = new Vector3(0f, 0.1f, 0f);

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;

        private void Start()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
        }

        private void LateUpdate()
        {
            if (_panel == null)
                return;

            bool moved = (transform.position - _lastPosition).sqrMagnitude > 1e-8f
                || Quaternion.Angle(transform.rotation, _lastRotation) > 0.05f;
            if (!moved)
                return;

            _lastPosition = transform.position;
            _lastRotation = transform.rotation;

            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            _panel.SetPositionAndRotation(transform.position + yaw * _panelOffset, yaw);
        }
    }
}
