using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // The Views page: one row per kind of visualisation, with a − and a + that make and unmake
    // copies of it. This is the page you use when one camera window is not enough - add a second
    // one here, then give it its own topic on the Topics tab.
    //
    // The robot's row is different in kind: arrows that pick WHICH robot, not a count. There is
    // only ever one robot - a second would stand in the room next to the first, and the arm the
    // calibration snaps onto has to be unambiguous - so the choice is a swap (RobotSelector).
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

        [Header("Robot")]
        [SerializeField] private RobotSelector _robots;
        [SerializeField] private TMP_Text _robotLabel;
        [SerializeField] private TMP_Text _robotIndex;
        [SerializeField] private Button _robotPrevious;
        [SerializeField] private Button _robotNext;

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

            if (_robotPrevious != null)
                _robotPrevious.onClick.AddListener(PreviousRobot);
            if (_robotNext != null)
                _robotNext.onClick.AddListener(NextRobot);
        }

        private void OnEnable()
        {
            // The selector raises Changed from its own Start as well, so the row is right even
            // if this page is the first thing shown
            if (_robots != null)
                _robots.Changed += Render;
            Render();
        }

        private void OnDisable()
        {
            if (_robots != null)
                _robots.Changed -= Render;
        }

        public void NextRobot()
        {
            if (_robots == null)
                return;

            _robots.Next(out string message);
            _actions?.ShowFeedback(message);
        }

        public void PreviousRobot()
        {
            if (_robots == null)
                return;

            _robots.Previous(out string message);
            _actions?.ShowFeedback(message);
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
            RenderRobot();

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

        private void RenderRobot()
        {
            int count = _robots != null ? _robots.Count : 0;

            if (_robotLabel != null)
            {
                // The topic rather than the bare namespace: it is what has to match the ROS side,
                // and it is the thing to check when the arm doesn't move
                string topic = count > 0 ? _robots.JointStateTopic : string.Empty;
                _robotLabel.text = count == 0
                    ? "<b>Robot</b>  <color=#FFB300>none wired</color>"
                    : $"<b>{_robots.LabelOf(_robots.Selected)}</b>\n" +
                      $"<size=75%><color=#9AA5B1>{(string.IsNullOrEmpty(topic) ? "no joint states" : topic)}</color></size>";
            }

            if (_robotIndex != null)
                _robotIndex.text = count > 0 ? $"{_robots.Selected + 1}/{count}" : string.Empty;

            if (_robotPrevious != null)
                _robotPrevious.interactable = count > 1;
            if (_robotNext != null)
                _robotNext.interactable = count > 1;
        }
    }
}
