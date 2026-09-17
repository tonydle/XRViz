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

        // Label on the TF anchoring button, rewritten to show which mode is live. The button says
        // what you'll get, not what you have: "Anchor: TF" means pressing it switches to TF.
        [SerializeField] private TMP_Text _tfButtonLabel;

        private float _feedbackExpiry;
        private float _tfLabelNextCheck;

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

        // Flip every TF-capable visualisation between TF placement and hand placement. All-or-
        // nothing on purpose: a scene with the laser on TF and the cloud by hand is showing you
        // two different worlds at once, and this button is the "put it all back" you want when
        // that turns out to be the case. Per-visualisation control lives on the panel's TF tab.
        public void ToggleTfAnchoring()
        {
            var anchors = FindAnchors();
            if (anchors.Length == 0)
            {
                ShowFeedback("<color=#FFB300>no TF anchors in the scene</color>");
                return;
            }

            // Mixed state resolves to "turn everything on", so one press always reaches a
            // consistent scene rather than inverting a mess into a different mess
            bool turnOn = false;
            foreach (var anchor in anchors)
            {
                if (!anchor.AnchorToTf)
                {
                    turnOn = true;
                    break;
                }
            }

            SetTfAnchoring(turnOn);
        }

        // Explicit pair for the Frames page. A single button whose label flips between
        // "Anchor: TF" and "Anchor: Manual" has to be read twice - once for the words, once to
        // remember whether it names the state or the action - and it is the state you want at a
        // glance while the robot is in front of you. Two buttons, each of which does exactly
        // what it says, cost one more press of panel space and no thinking at all.
        public void AnchorAllTf()
        {
            SetTfAnchoring(true);
        }

        public void AnchorAllManual()
        {
            SetTfAnchoring(false);
        }

        public void SetTfAnchoring(bool anchorToTf)
        {
            var anchors = FindAnchors();
            foreach (var anchor in anchors)
                anchor.SetAnchorToTf(anchorToTf);

            RenderTfButtonLabel(anchors);

            if (!anchorToTf)
            {
                ShowFeedback($"hand placement · {Count(anchors.Length, "anchor")}");
                return;
            }

            // SetAnchorToTf places immediately, so these statuses are this frame's, not last
            // frame's - which matters because "TF on" with nothing resolving looks identical to
            // "TF on" and working until you notice nothing moved
            int placed = 0;
            foreach (var anchor in anchors)
            {
                if (anchor.Status == TfAnchor.AnchorStatus.Anchored)
                    placed++;
            }

            if (placed == anchors.Length)
                ShowFeedback($"TF placement · {Count(placed, "anchor")}");
            else if (placed == 0)
                ShowFeedback($"<color=#FFB300>TF on, but no frame resolved</color> — {DescribeFailures(anchors)}");
            else
                ShowFeedback($"TF placement · {placed}/{anchors.Length} placed — {DescribeFailures(anchors)}");
        }

        // The first thing that went wrong, named. Every failure here has a different fix, and
        // "0 placed" alone doesn't tell you which one you're looking at.
        private static string DescribeFailures(TfAnchor[] anchors)
        {
            foreach (var anchor in anchors)
            {
                switch (anchor.Status)
                {
                    case TfAnchor.AnchorStatus.NoTfTree:
                        return "no TF Origin in the scene";
                    case TfAnchor.AnchorStatus.NoFrameId:
                        return $"{anchor.Label}: no message yet";
                    case TfAnchor.AnchorStatus.FrameNotInTf:
                        return $"{anchor.Label}: '{anchor.FrameId}' not in /tf";
                }
            }
            return "see the TF tab";
        }

        private static TfAnchor[] FindAnchors()
        {
            return FindObjectsByType<TfAnchor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        private void RenderTfButtonLabel(TfAnchor[] anchors)
        {
            if (_tfButtonLabel == null)
                return;

            bool anyManual = false;
            foreach (var anchor in anchors)
            {
                if (!anchor.AnchorToTf)
                {
                    anyManual = true;
                    break;
                }
            }

            _tfButtonLabel.text = anyManual ? "Anchor: TF" : "Anchor: Manual";
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
            // The TF Anchors panel can flip anchors individually behind this button's back, so
            // the label is re-derived rather than only written when this script acts. Four times
            // a second is well under the rate anyone notices and costs one find of a handful of
            // components.
            if (_tfButtonLabel != null && Time.unscaledTime >= _tfLabelNextCheck)
            {
                _tfLabelNextCheck = Time.unscaledTime + 0.25f;
                RenderTfButtonLabel(FindAnchors());
            }

            if (_feedbackExpiry <= 0f || Time.unscaledTime < _feedbackExpiry)
                return;

            _feedbackExpiry = 0f;
            if (_feedback != null)
                _feedback.text = string.Empty;
        }
    }
}
