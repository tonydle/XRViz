using UnityEngine;

namespace Unity.Robotics
{
    // Lives on the ROS control panel group root - which stays active so Update keeps polling -
    // and shows or hides the panel below it with the left controller's Menu button.
    //
    // Toggles Canvas.enabled rather than the GameObjects, so the page the tabs component has
    // open survives being hidden and comes back as you left it.
    //
    // Summoning: the panel is world-placed and grabbable, which is right up until you walk to the
    // other side of the robot and press Menu expecting a panel. Rather than either leaving it
    // stranded or yanking it to your face every time (which throws away a placement you chose on
    // purpose), showing it checks whether it is actually findable from where you are standing -
    // too far, or too far off to the side - and only then brings it to you.
    public class ControlPanelMenuToggle : MonoBehaviour
    {
        [Tooltip("Bring the panel in front of the viewer when it is shown from somewhere it " +
                 "cannot be seen. Off leaves it wherever it was last placed.")]
        [SerializeField] private bool _summonWhenOutOfView = true;

        [Tooltip("Degrees off the viewing direction past which the panel counts as out of view.")]
        [SerializeField] private float _summonBeyondAngle = 55f;

        [Tooltip("Metres from the viewer past which the panel counts as out of reach, however " +
                 "well centred it is.")]
        [SerializeField] private float _summonBeyondDistance = 2.5f;

        [Tooltip("Where a summoned panel lands: metres in front of the viewer, and metres below " +
                 "eye level (the panel is about 0.66 m tall, so a little below eye level puts " +
                 "its header at eye level).")]
        [SerializeField] private float _summonDistance = 0.85f;
        [SerializeField] private float _summonDrop = 0.12f;

        private Canvas[] _canvases;
        private ControlPanelTabs _tabs;
        private Transform _head;
        private bool _visible;

        public bool Visible => _visible;

        private void Start()
        {
            // Include inactive: pages that start hidden must still be picked up, or their
            // Canvas - if any ever gains one - would stay enabled behind a hidden panel
            _canvases = GetComponentsInChildren<Canvas>(true);
            _tabs = GetComponentInChildren<ControlPanelTabs>(true);
            SetVisible(false);
        }

        private void Update()
        {
            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
                Toggle();
        }

        public void Toggle()
        {
            SetVisible(!_visible);
        }

        // Wired to the rail's Hide button. Controllers have the Menu button; a hand-tracking
        // user has no equivalent, so the panel needs a way out of it that is on the panel.
        public void Hide()
        {
            SetVisible(false);
        }

        public void Show()
        {
            SetVisible(true);
        }

        private void SetVisible(bool visible)
        {
            _visible = visible;

            if (visible)
            {
                if (_summonWhenOutOfView && !IsFindable())
                    SummonToViewer();

                // Re-enter the current page so anything it polls is re-read on the way in
                if (_tabs != null)
                    _tabs.Reopen();
            }

            foreach (var canvas in _canvases)
            {
                if (canvas != null)
                    canvas.enabled = visible;
            }
        }

        private bool IsFindable()
        {
            var head = ResolveHead();
            if (head == null)
                return true; // nothing to measure against; leave the panel alone

            Vector3 toPanel = transform.position - head.position;
            if (toPanel.sqrMagnitude > _summonBeyondDistance * _summonBeyondDistance)
                return false;

            return Vector3.Angle(head.forward, toPanel) <= _summonBeyondAngle;
        }

        private void SummonToViewer()
        {
            var head = ResolveHead();
            if (head == null)
                return;

            // Flattened forward: a panel placed along a downward gaze would otherwise land on
            // the floor, and one placed along an upward gaze on the ceiling
            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            forward.Normalize();

            Vector3 position = head.position + forward * _summonDistance - Vector3.up * _summonDrop;
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);

            // Move it through its own handle where it has one, so the grab sphere comes with the
            // panel instead of staying behind and snapping it back on the next grab
            var handle = FindHandle();
            if (handle != null)
            {
                handle.SetTargetPose(position, rotation);
                handle.SnapToTarget();
            }
            else
            {
                transform.SetPositionAndRotation(position, rotation);
            }
        }

        private PlacementHandle FindHandle()
        {
            var handles = FindObjectsByType<PlacementHandle>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var handle in handles)
            {
                if (handle.Target == transform)
                    return handle;
            }
            return null;
        }

        private Transform ResolveHead()
        {
            if (_head == null && Camera.main != null)
                _head = Camera.main.transform;
            return _head;
        }
    }
}
