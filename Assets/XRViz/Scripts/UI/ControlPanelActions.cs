using TMPro;
using UnityEngine;

namespace Unity.Robotics
{
    // The ROS control panel's scene-wide buttons - the ones that act on things outside the panel,
    // rather than on the connection itself (that's RosConnectionStatusUI).
    //
    // Targets are found at press time rather than serialized, so a laser scan or a placement
    // handle added to the scene by hand after the generator ran is picked up too. A find per
    // button press is irrelevant next to how rarely these are pressed.
    public class ControlPanelActions : MonoBehaviour
    {
        // Transient line under the buttons confirming what a press did. Without it "Clear Scan"
        // on an already-empty scan is indistinguishable from a dead button.
        [SerializeField] private TMP_Text _feedback;
        [SerializeField] private float _feedbackSeconds = 2.5f;

        private float _feedbackExpiry;

        // Every visualisation that holds geometry between messages - laser scan, point cloud,
        // anything added later that implements IClearableVisualization. Unity can't search for
        // an interface directly, hence the sweep over MonoBehaviours; it costs nothing at the
        // rate a button gets pressed.
        public void ClearVisualizations()
        {
            var behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            int cleared = 0;
            foreach (var behaviour in behaviours)
            {
                if (behaviour is not IClearableVisualization visualization)
                    continue;
                visualization.Clear();
                cleared++;
            }

            ShowFeedback(cleared == 0
                ? "<color=#FFB300>nothing to clear</color>"
                : $"cleared {Count(cleared, "visualisation")}");
        }

        // Note this moves the control panel too - its own handle is one of the anchors. That's
        // the point: the panel is as easy to fling out of reach with ray grab as anything else.
        public void ResetAnchors()
        {
            var handles = FindObjectsByType<PlacementHandle>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var handle in handles)
                handle.ResetToDefault();

            ShowFeedback(handles.Length == 0
                ? "<color=#FFB300>no placement handles found</color>"
                : $"reset {Count(handles.Length, "anchor")}");
        }

        private static string Count(int n, string noun)
        {
            return n == 1 ? $"1 {noun}" : $"{n} {noun}s";
        }

        private void ShowFeedback(string message)
        {
            if (_feedback == null)
                return;
            _feedback.text = message;
            _feedbackExpiry = Time.unscaledTime + _feedbackSeconds;
        }

        private void Update()
        {
            if (_feedbackExpiry <= 0f || Time.unscaledTime < _feedbackExpiry)
                return;

            _feedbackExpiry = 0f;
            if (_feedback != null)
                _feedback.text = string.Empty;
        }
    }
}
