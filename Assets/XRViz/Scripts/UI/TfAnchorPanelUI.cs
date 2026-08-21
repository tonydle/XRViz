using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // Popup listing every TfAnchor in the scene: what frame it would place by, whether that frame
    // is actually in the TF tree, and a press to flip that one visualisation between TF and hand
    // placement. The control panel's own button does all of them at once; this is for the case
    // where the arm's frames are trustworthy but the camera's mount isn't calibrated yet, which
    // is most of the time on a rig being brought up.
    //
    // Anchors are found at open time rather than serialized, matching ControlPanelActions - one
    // added to the scene by hand shows up here without the generator being re-run. Rows are a
    // fixed set of buttons like the topic browser's, with no paging: past six anchors in one scene
    // the room is the problem, not the panel.
    public class TfAnchorPanelUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text _status;
        [SerializeField] private Button[] _rows;

        // Statuses change on their own as topics arrive and TF resolves, so the list re-renders
        // on a timer rather than only on a press
        [SerializeField] private float _refreshSeconds = 0.5f;

        private readonly List<TfAnchor> _anchors = new List<TfAnchor>();
        private TMP_Text[] _rowLabels;
        private float _nextRefresh;

        private int RowCount => _rows != null ? _rows.Length : 0;

        private void Awake()
        {
            _rowLabels = new TMP_Text[RowCount];
            for (int i = 0; i < RowCount; i++)
            {
                _rowLabels[i] = _rows[i].GetComponentInChildren<TMP_Text>();
                int rowIndex = i; // capture per row, not the shared loop variable
                _rows[i].onClick.AddListener(() => ToggleRow(rowIndex));
            }
        }

        private void OnEnable()
        {
            Rebuild();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + _refreshSeconds;
            Render();
        }

        public void Rebuild()
        {
            _anchors.Clear();
            _anchors.AddRange(FindObjectsByType<TfAnchor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
            Render();
        }

        public void AllTf()
        {
            SetAll(true);
        }

        public void AllManual()
        {
            SetAll(false);
        }

        public void ToggleVisibility()
        {
            gameObject.SetActive(!gameObject.activeSelf);
        }

        private void SetAll(bool anchorToTf)
        {
            foreach (var anchor in _anchors)
                anchor.SetAnchorToTf(anchorToTf);
            Render();
        }

        private void ToggleRow(int rowIndex)
        {
            if (rowIndex >= _anchors.Count)
                return;

            _anchors[rowIndex].ToggleAnchorToTf();
            Render();
        }

        private void Render()
        {
            RenderStatus();

            for (int i = 0; i < RowCount; i++)
            {
                bool hasAnchor = i < _anchors.Count;
                // Empty rows stay in place but dead, so the list doesn't reflow under the ray
                _rows[i].interactable = hasAnchor;
                if (_rowLabels[i] == null)
                    continue;

                if (!hasAnchor)
                {
                    _rowLabels[i].text = string.Empty;
                    continue;
                }

                _rowLabels[i].text = DescribeAnchor(_anchors[i]);
            }
        }

        // One line per anchor: what it is, what frame it would use, and what that's currently
        // doing. The frame name is the useful half - "which frame is this even in" is the first
        // question when a cloud lands somewhere unexpected.
        private static string DescribeAnchor(TfAnchor anchor)
        {
            string frame = string.IsNullOrEmpty(anchor.FrameId) ? "<no frame>" : anchor.FrameId;
            string state;

            switch (anchor.Status)
            {
                case TfAnchor.AnchorStatus.Anchored:
                    state = "<color=#4CAF50>TF</color>";
                    break;
                case TfAnchor.AnchorStatus.Manual:
                    state = "<color=#9AA5B1>manual</color>";
                    break;
                case TfAnchor.AnchorStatus.NoFrameId:
                    state = "<color=#FFB300>no msg</color>";
                    break;
                case TfAnchor.AnchorStatus.FrameNotInTf:
                    state = "<color=#FFB300>not in /tf</color>";
                    break;
                default:
                    state = "<color=#FF5252>no TF origin</color>";
                    break;
            }

            return $"<b>{anchor.Label}</b>  <size=80%>{frame}</size>\n<size=80%>{state}</size>";
        }

        private void RenderStatus()
        {
            if (_status == null)
                return;

            var tree = RosTfTree.Instance;
            if (tree == null)
            {
                _status.text = "<color=#FF5252>no TF Origin in the scene</color>";
                return;
            }

            string fixedFrame = string.IsNullOrEmpty(tree.FixedFrame)
                ? "each chain's root"
                : tree.FixedFrame;

            if (!tree.HasFrames)
            {
                _status.text = $"fixed frame: {fixedFrame}\n<color=#FFB300>no /tf received yet</color>";
                return;
            }

            _status.text = $"fixed frame: {fixedFrame}\n{tree.FrameCount} frames known";
        }
    }
}
