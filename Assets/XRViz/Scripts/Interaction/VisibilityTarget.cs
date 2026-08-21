using UnityEngine;

namespace Unity.Robotics
{
    // Marks a top-level visualisation (or the robot) as something the Visibility panel can show
    // or hide. Visibility is just the target GameObject's active state - everything on it
    // (subscriber, visualizer, TfAnchor) stops running while hidden and picks back up once shown,
    // the same as any other deactivated MonoBehaviour would.
    public class VisibilityTarget : MonoBehaviour
    {
        [SerializeField] private string _label;
        [SerializeField] private GameObject _target;

        public string Label => _label;
        public bool Visible => _target != null && _target.activeSelf;

        private void Reset()
        {
            _target = gameObject;
        }

        public void SetVisible(bool visible)
        {
            if (_target != null)
                _target.SetActive(visible);
        }

        public void ToggleVisible()
        {
            SetVisible(!Visible);
        }
    }
}
