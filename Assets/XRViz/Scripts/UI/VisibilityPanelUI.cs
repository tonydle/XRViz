using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // Popup listing every VisibilityTarget in the scene (robot, laser scan, point cloud, ...)
    // with a press per row to show or hide it. Targets are found at open time rather than
    // serialized, matching TfAnchorPanelUI, so anything added to the scene by hand after the
    // generator ran shows up here too. No per-frame refresh: unlike the TF panel, nothing but
    // this panel's own buttons changes a target's visibility, so re-rendering after each press
    // is enough.
    public class VisibilityPanelUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text _status;
        [SerializeField] private Button[] _rows;

        private readonly List<VisibilityTarget> _targets = new List<VisibilityTarget>();
        private TMP_Text[] _rowLabels;

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

        public void Rebuild()
        {
            _targets.Clear();
            _targets.AddRange(FindObjectsByType<VisibilityTarget>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
            Render();
        }

        public void ShowAll()
        {
            SetAll(true);
        }

        public void HideAll()
        {
            SetAll(false);
        }

        public void ToggleVisibility()
        {
            gameObject.SetActive(!gameObject.activeSelf);
        }

        private void SetAll(bool visible)
        {
            foreach (var target in _targets)
                target.SetVisible(visible);
            Render();
        }

        private void ToggleRow(int rowIndex)
        {
            if (rowIndex >= _targets.Count)
                return;

            _targets[rowIndex].ToggleVisible();
            Render();
        }

        private void Render()
        {
            RenderStatus();

            for (int i = 0; i < RowCount; i++)
            {
                bool hasTarget = i < _targets.Count;
                // Empty rows stay in place but dead, so the list doesn't reflow under the ray
                _rows[i].interactable = hasTarget;
                if (_rowLabels[i] == null)
                    continue;

                if (!hasTarget)
                {
                    _rowLabels[i].text = string.Empty;
                    continue;
                }

                var target = _targets[i];
                string state = target.Visible
                    ? "<color=#4CAF50>shown</color>"
                    : "<color=#9AA5B1>hidden</color>";
                _rowLabels[i].text = $"{target.Label}   {state}";
            }
        }

        private void RenderStatus()
        {
            if (_status == null)
                return;

            if (_targets.Count == 0)
            {
                _status.text = "<color=#FFB300>no visualisations in the scene</color>";
                return;
            }

            int shown = 0;
            foreach (var target in _targets)
            {
                if (target.Visible)
                    shown++;
            }

            _status.text = $"{shown} / {_targets.Count} shown";
        }
    }
}
