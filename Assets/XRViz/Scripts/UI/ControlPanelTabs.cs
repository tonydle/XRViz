using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // One panel, one page at a time, with a rail of tabs down the left.
    //
    // This replaces five free-floating popup Canvases that used to hang off the control panel in
    // a cross - topics to the left, frames below them, keypad to the right, visibility below
    // that, calibration a further 0.9 m out. Each opened independently, so several could be up at
    // once, the group spanned about two metres of room, and the calibration panel - the one you
    // use while walking around looking at the real robot - was far enough off-axis to need
    // turning your head away from the thing you were calibrating against.
    //
    // Pages here are plain RectTransforms under the panel's single Canvas, NOT nested Canvases.
    // That is a hard requirement, not a preference: PointableCanvasModule discards any raycast
    // hit whose Canvas.rootCanvas isn't the exact Canvas injected into the PointableCanvas, and a
    // nested Canvas reports its outermost ancestor as its rootCanvas - so a nested page's buttons
    // silently stop receiving hits. One Canvas also means one ray interaction rig instead of six
    // competing ones.
    //
    // A page is shown by activating its GameObject, so a page's own OnEnable/OnDisable still
    // fires: the topic browser still refreshes its list when opened, and the calibration page
    // still cancels a running search when you navigate away from it.
    public class ControlPanelTabs : MonoBehaviour
    {
        [SerializeField] private GameObject[] _pages;

        // Parallel to _pages for as far as it goes. Pages past the end of this array are
        // reachable but have no tab - the IP keypad is one, opened from the ROS page's Edit IP
        // button, because a keypad is a step inside a task rather than a place you go.
        [SerializeField] private Button[] _tabs;

        [SerializeField] private int _defaultPage;

        // The panel header's title, rewritten per page. The rail has room for one short word per
        // tab ("TF", "Scene"); the header has room to say what that word means, and saying it
        // where you have just arrived is cheaper than a wider rail on every page.
        [SerializeField] private TMP_Text _title;
        [SerializeField] private string[] _pageTitles;

        // Tint for the tab whose page is showing. The rest of the rail sits at the button's own
        // neutral colour, so the difference reads as "you are here" rather than as decoration.
        [SerializeField] private Color _activeTint = new Color(0.15f, 0.31f, 0.50f);
        [SerializeField] private Color _inactiveTint = new Color(0.13f, 0.14f, 0.17f);

        private static readonly Color k_ActiveText = new Color(1f, 1f, 1f);
        private static readonly Color k_InactiveText = new Color(0.62f, 0.67f, 0.73f);

        private int _current = -1;

        public int CurrentPage => _current;

        private void Awake()
        {
            // Whatever the scene was saved with, start from a known page with exactly one
            // page live - a saved scene that happened to have two pages active would otherwise
            // draw them on top of each other until the first press.
            ShowPage(_defaultPage);
        }

        public void ShowPage(int index)
        {
            if (_pages == null || _pages.Length == 0)
                return;

            index = Mathf.Clamp(index, 0, _pages.Length - 1);
            _current = index;

            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] != null && _pages[i].activeSelf != (i == index))
                    _pages[i].SetActive(i == index);
            }

            RenderTabs();

            if (_title != null && _pageTitles != null && index < _pageTitles.Length)
                _title.text = _pageTitles[index];
        }

        // For the panel being re-opened: come back to the page you were on, but re-run the
        // page's OnEnable so anything it polls (topic lists, anchor states) is fresh.
        public void Reopen()
        {
            ShowPage(_current < 0 ? _defaultPage : _current);
        }

        private void RenderTabs()
        {
            if (_tabs == null)
                return;

            for (int i = 0; i < _tabs.Length; i++)
            {
                if (_tabs[i] == null)
                    continue;

                bool active = i == _current;

                var colors = _tabs[i].colors;
                colors.normalColor = active ? _activeTint : _inactiveTint;
                colors.highlightedColor = Lighten(active ? _activeTint : _inactiveTint, 0.22f);
                colors.pressedColor = Lighten(active ? _activeTint : _inactiveTint, -0.18f);
                colors.selectedColor = colors.normalColor;
                _tabs[i].colors = colors;

                var label = _tabs[i].GetComponentInChildren<TMP_Text>();
                if (label != null)
                    label.color = active ? k_ActiveText : k_InactiveText;
            }
        }

        private static Color Lighten(Color color, float amount)
        {
            return amount >= 0f
                ? Color.Lerp(color, Color.white, amount)
                : Color.Lerp(color, Color.black, -amount);
        }
    }
}
