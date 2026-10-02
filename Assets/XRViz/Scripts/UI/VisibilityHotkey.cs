using UnityEngine;

namespace Unity.Robotics
{
    // Binds a controller button to one VisibilityTarget's show/hide, so a visualisation that is
    // in the way can be dismissed without finding the panel, opening the Scene tab and aiming at
    // a row. Y (left controller) hides and shows the camera image window by default.
    //
    // This cannot live on the thing it hides. VisibilityTarget works by deactivating its
    // GameObject, and a deactivated object's Update never runs, so a window that hid itself
    // could never hear the button that brings it back. It goes on the control panel group root
    // instead, which stays active for exactly the same reason ControlPanelMenuToggle does.
    //
    // The target is found by label rather than by a serialized reference, matching
    // ControlPanelActions: the labels are already the names the Scene tab lists, and a
    // hand-added window is picked up without rewiring anything.
    public class VisibilityHotkey : MonoBehaviour
    {
        [Tooltip("Label of the VisibilityTarget to toggle - the same text the panel's Scene tab " +
                 "lists it under.")]
        [SerializeField] private string _targetLabel = "Camera Image";

        [Tooltip("Button that toggles it. On LTouch, Two is Y and One is X; on RTouch they are " +
                 "B and A. Start is the Menu button and is already taken by the panel.")]
        [SerializeField] private OVRInput.Button _button = OVRInput.Button.Two;

        [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.LTouch;

        [Tooltip("Hide the grab handle along with the window. Off leaves a grabbable sphere " +
                 "floating with nothing on the end of it.")]
        [SerializeField] private bool _hideHandle = true;

        private VisibilityTarget _target;
        private PlacementHandle _handle;

        private void Update()
        {
            if (OVRInput.GetDown(_button, _controller))
                Toggle();
        }

        // Also callable from a UI button, for a hand-tracking user with no controller to press
        public void Toggle()
        {
            var target = ResolveTarget();
            if (target == null)
            {
                Debug.LogWarning($"[XRViz] {nameof(VisibilityHotkey)}: no VisibilityTarget " +
                    $"labelled '{_targetLabel}', so the button has nothing to toggle.", this);
                return;
            }

            bool visible = !target.Visible;
            target.SetVisible(visible);

            if (!_hideHandle)
                return;

            // Suspending rather than deactivating: it hides the sphere AND stops its writes, so a
            // handle that was nudged while the window was away doesn't drag the window with it on
            // the way back. Safe here only because this window is deliberately never TF-placed -
            // on anything carrying a TfAnchor the two would fight over the same suspension flag.
            var handle = ResolveHandle(target);
            if (handle != null)
                handle.SetSuspended(!visible);
        }

        private VisibilityTarget ResolveTarget()
        {
            // Re-resolve when the cached one has gone: the scene generator destroys and rebuilds
            // these objects, and a stale reference would silently stop the button working
            if (_target != null)
                return _target;

            var targets = FindObjectsByType<VisibilityTarget>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var candidate in targets)
            {
                if (candidate.Label == _targetLabel)
                {
                    _target = candidate;
                    break;
                }
            }
            return _target;
        }

        private PlacementHandle ResolveHandle(VisibilityTarget target)
        {
            if (_handle != null)
                return _handle;

            var handles = FindObjectsByType<PlacementHandle>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var candidate in handles)
            {
                if (candidate.Target == target.transform)
                {
                    _handle = candidate;
                    break;
                }
            }
            return _handle;
        }
    }
}
