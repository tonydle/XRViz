using TMPro;
using UnityEngine;

namespace Unity.Robotics
{
    public class IpKeypadUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text _display;
        [SerializeField] private RosConnectionStatusUI _statusUi;

        public void AppendChar(string c)
        {
            if (_display != null)
                _display.text += c;
        }

        public void Backspace()
        {
            if (_display == null || _display.text.Length == 0)
                return;
            _display.text = _display.text.Substring(0, _display.text.Length - 1);
        }

        public void Clear()
        {
            if (_display != null)
                _display.text = "";
        }

        public void Apply()
        {
            if (_statusUi == null || _display == null || string.IsNullOrEmpty(_display.text))
                return;
            _statusUi.SetIpAddress(_display.text);
            _statusUi.Connect();
            gameObject.SetActive(false);
        }

        public void ToggleVisibility()
        {
            gameObject.SetActive(!gameObject.activeSelf);
        }
    }
}
