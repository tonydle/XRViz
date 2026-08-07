using UnityEngine;

namespace Unity.Robotics
{
    // Lives on the ROS control panel group root - which stays active so Update keeps polling -
    // and shows/hides the panel's Canvases below it. Toggles Canvas.enabled rather than the
    // GameObjects so the keypad's own shown/hidden state (IpKeypadUI drives that with
    // SetActive) stays independent of whether the panel as a whole is visible.
    public class ControlPanelMenuToggle : MonoBehaviour
    {
        private Canvas[] _canvases;
        private bool _visible;

        private void Start()
        {
            // Include inactive: the keypad Canvas starts hidden and must still be picked up
            _canvases = GetComponentsInChildren<Canvas>(true);
            SetVisible(false);
        }

        private void Update()
        {
            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
                SetVisible(!_visible);
        }

        private void SetVisible(bool visible)
        {
            _visible = visible;
            foreach (var canvas in _canvases)
            {
                if (canvas != null)
                    canvas.enabled = visible;
            }
        }
    }
}
