using UnityEngine;

namespace Unity.Robotics
{
    [RequireComponent(typeof(Canvas))]
    public class ControlPanelMenuToggle : MonoBehaviour
    {
        private Canvas _canvas;

        private void Start()
        {
            _canvas = GetComponent<Canvas>();
            _canvas.enabled = false;
        }

        private void Update()
        {
            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
                _canvas.enabled = !_canvas.enabled;
        }
    }
}
