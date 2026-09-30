using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // The Views page: one row per kind of visualisation, with a − and a + that make and unmake
    // copies of it. This is the page you use when one camera window is not enough - add a second
    // one here, then give it its own topic on the Topics tab.
    //
    // The robot deliberately has no row. There is one robot: a second copy would subscribe to a
    // second /joint_states and stand in the room next to the first, which is not a thing anyone
    // wants, and the arm the calibration snaps onto has to be unambiguous.
    //
    // The count includes the scene's own instance, so it never reads zero - the floor is one, and
    // the − on a kind that has no copies says so rather than quietly doing nothing. Rows are a
    // fixed set like every other list in this panel, not a layout group: there are as many rows
    // as the generator wired kinds, and that number changes when the scene is regenerated, not
    // while anyone is wearing the headset.
    public class VisualizationCountPanelUI : MonoBehaviour
    {
        [SerializeField] private VisualizationSpawner _spawner;

        // Only for its footer line. A press here changes the scene rather than this page, so the
        // confirmation belongs in the one place the panel always puts it.
        [SerializeField] private ControlPanelActions _actions;

        [SerializeField] private TMP_Text _status;

        [SerializeField] private TMP_Text[] _rowLabels;
        [SerializeField] private TMP_Text[] _countLabels;
        [SerializeField] private Button[] _addButtons;
        [SerializeField] private Button[] _removeButtons;

        private int RowCount => _rowLabels != null ? _rowLabels.Length : 0;

        private void Awake()
        {
            for (int i = 0; i < RowCount; i++)
            {
                int row = i; // capture per row, not the shared loop variable

                if (_addButtons != null && i < _addButtons.Length && _addButtons[i] != null)
                    _addButtons[i].onClick.AddListener(() => Add(row));

                if (_removeButtons != null && i < _removeButtons.Length && _removeButtons[i] != null)
                    _removeButtons[i].onClick.AddListener(() => Remove(row));
            }
        }

        private void OnEnable()
        {
            Render();
        }

        public void Add(int index)
        {
            if (_spawner == null)
                return;

            _spawner.Add(index, out string message);
            _actions?.ShowFeedback(message);
            Render();
        }

        public void Remove(int index)
        {
            if (_spawner == null)
                return;

            _spawner.Remove(index, out string message);
            _actions?.ShowFeedback(message);
            Render();
        }

        private void Render()
        {
            int kinds = _spawner != null ? _spawner.KindCount : 0;
            int total = 0;

            for (int i = 0; i < RowCount; i++)
            {
                bool hasKind = i < kinds;

                if (_rowLabels[i] != null)
                    _rowLabels[i].text = hasKind ? $"<b>{_spawner.LabelOf(i)}</b>" : string.Empty;

                int count = hasKind ? _spawner.CountOf(i) : 0;
                total += count;

                if (_countLabels != null && i < _countLabels.Length && _countLabels[i] != null)
                    _countLabels[i].text = hasKind ? count.ToString() : string.Empty;

                // Dead rather than hidden, so the page does not reflow under a ray that is
                // already pointing at it
                if (_addButtons != null && i < _addButtons.Length && _addButtons[i] != null)
                    _addButtons[i].interactable = hasKind && _spawner.CanAdd(i);

                if (_removeButtons != null && i < _removeButtons.Length && _removeButtons[i] != null)
                    _removeButtons[i].interactable = hasKind && _spawner.CanRemove(i);
            }

            if (_status == null)
                return;

            if (kinds == 0)
            {
                _status.text = "<color=#FFB300>nothing wired to duplicate</color>";
                return;
            }

            _status.text = $"{total} visualisation{(total == 1 ? "" : "s")} in the scene";
        }
    }
}
