using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // Popup that drives ArucoRobotCalibrator: one button to start looking for the tag, a status
    // line, and the Apply / Cancel pair that only appears once there is something to agree with.
    //
    // Buttons are wired straight to the calibrator by the scene generator; this component owns
    // only what the panel SHOWS. That split matters because the calibrator can move between
    // states on its own - it times out, and it finishes a search without being pressed again -
    // so the panel has to follow the calibrator rather than assume the last press is still the
    // current state.
    //
    // Apply and Cancel are hidden rather than merely disabled while there is nothing to confirm.
    // At arm's length through passthrough, a greyed-out button and a live one are not reliably
    // distinguishable, and pressing the wrong one here throws away a calibration.
    public class CalibrationPanelUI : MonoBehaviour
    {
        [SerializeField] private ArucoRobotCalibrator _calibrator;
        [SerializeField] private TMP_Text _status;

        // Shown when idle, and re-labelled to "Search again" once a calibration has been applied
        // or has timed out, so it reads as a retry rather than as a first attempt
        [SerializeField] private Button _startButton;
        [SerializeField] private Button _applyButton;
        [SerializeField] private Button _cancelButton;

        // Fills as the ten seconds run down, so a search that is finding nothing still looks
        // alive. Without it the panel sits on one sentence and reads as frozen.
        [SerializeField] private Image _progressFill;

        private TMP_Text _startLabel;
        private ArucoRobotCalibrator.CalibrationState _lastState = (ArucoRobotCalibrator.CalibrationState)(-1);

        private void Awake()
        {
            if (_startButton != null)
                _startLabel = _startButton.GetComponentInChildren<TMP_Text>();
        }

        private void OnEnable()
        {
            // Force a render: the state may well have changed while the panel was closed
            _lastState = (ArucoRobotCalibrator.CalibrationState)(-1);
            Render();
        }

        // A calibration left mid-search when the panel is closed would keep the camera open and
        // keep moving the robot with nothing on screen to say so
        private void OnDisable()
        {
            if (_calibrator != null && _calibrator.IsBusy)
                _calibrator.CancelCalibration();
        }

        public void ToggleVisibility()
        {
            gameObject.SetActive(!gameObject.activeSelf);
        }

        private void Update()
        {
            Render();
        }

        private void Render()
        {
            if (_calibrator == null)
            {
                if (_status != null)
                    _status.text = "<color=#FFB300>No calibrator wired to this panel.</color>";
                return;
            }

            var state = _calibrator.State;
            bool searching = state == ArucoRobotCalibrator.CalibrationState.Searching;
            bool confirming = state == ArucoRobotCalibrator.CalibrationState.AwaitingConfirmation;

            if (_progressFill != null)
            {
                // The track is the fill's parent, so hiding it hides both
                var track = _progressFill.transform.parent.gameObject;
                if (track.activeSelf != searching)
                    track.SetActive(searching);

                if (searching)
                    _progressFill.fillAmount = _calibrator.SearchProgress;
            }

            if (state != _lastState)
            {
                _lastState = state;

                if (_startButton != null)
                    _startButton.gameObject.SetActive(!searching && !confirming);
                if (_applyButton != null)
                    _applyButton.gameObject.SetActive(confirming);
                if (_cancelButton != null)
                    _cancelButton.gameObject.SetActive(searching || confirming);

                if (_startLabel != null)
                {
                    _startLabel.text = state == ArucoRobotCalibrator.CalibrationState.Idle
                        ? "Find Tag"
                        : "Search Again";
                }
            }

            if (_status == null)
                return;

            string message = _calibrator.StatusMessage;
            if (string.IsNullOrEmpty(message))
            {
                _status.text = "Print the chassis tag, stand where you can see it, then press " +
                               "Find Tag.";
                return;
            }

            switch (state)
            {
                case ArucoRobotCalibrator.CalibrationState.TimedOut:
                case ArucoRobotCalibrator.CalibrationState.Unavailable:
                    _status.text = $"<color=#FFB300>{message}</color>";
                    break;
                case ArucoRobotCalibrator.CalibrationState.Applied:
                    _status.text = $"<color=#7BD88F>{message}</color>";
                    break;
                default:
                    _status.text = message;
                    break;
            }
        }
    }
}
