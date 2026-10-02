using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // Lists the topics the ros_tcp_endpoint is advertising and lets one be picked from inside the
    // headset, rebinding a subscriber live.
    //
    // Handles several subscribers: pick which visualisation you're configuring with the target
    // selector, then pick its topic. The list is filtered to the selected target's message type,
    // so you can't wire sensor_msgs/LaserScan into a JointState subscriber by accident.
    //
    // Targets are FOUND rather than serialized, matching ControlPanelActions and the TF and Scene
    // pages. That is what lets the Views page add a second camera window at runtime and have it
    // turn up here to be given a topic; a serialized list could only ever name what existed when
    // the scene was generated. They are sorted by hierarchy path, which groups a visualisation's
    // subscribers under it ("RGBd Camera 2 Color" next to "RGBd Camera 2 Depth") and keeps the
    // order stable - FindObjectsByType makes no promise about its own.
    //
    // Paged rather than scrolled: a fixed set of row buttons is far easier to hit with a ray than
    // a ScrollRect, and needs no mask or layout group. Rows are recycled - labels are rewritten
    // per page.
    public class TopicBrowserUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text _targetLabel;
        [SerializeField] private TMP_Text _status;
        [SerializeField] private TMP_Text _pageLabel;

        // Label on the type-filter button, rewritten to say which way it is set. "Where is my
        // topic" is nearly always the filter, and the answer has to be reachable from inside the
        // headset rather than from a tickbox in the Inspector.
        [SerializeField] private TMP_Text _typeFilterLabel;

        [SerializeField] private Button[] _rows;

        // GetTopicAndTypeList has no failure path - if the endpoint isn't connected the request
        // just vanishes and the callback never fires. Without our own deadline the panel would
        // sit on "requesting…" forever with no way to tell that apart from a slow reply.
        [SerializeField] private float _responseTimeout = 5f;

        // Show every topic rather than only those matching the target's message type.
        // Off by default: subscribing to a mismatched type gets you deserialization errors.
        [SerializeField] private bool _showAllTypes = false;

        // Untick if the endpoint can't survive a __topic_list request. Some ros_tcp_endpoint
        // builds raise inside send_topic_list ("AttributeError: 'NoneType' object has no
        // attribute 'msg'"), which kills the client thread and drops the whole TCP connection -
        // so merely opening this panel would cost you every subscription in the scene. With this
        // off the list is only fetched when you press Refresh.
        [SerializeField] private bool _refreshOnOpen = true;

        private readonly List<IRosTopicBinding> _bindings = new List<IRosTopicBinding>();

        // GameObject name per binding. Message type alone stopped being enough to tell targets
        // apart once the point cloud arrived with two sensor_msgs/Image subscribers - both would
        // read "Image", and picking the wrong one silently retargets colour instead of depth.
        private readonly List<string> _bindingNames = new List<string>();

        private readonly List<string> _topics = new List<string>();

        // Last full reply, kept so switching target re-filters instantly instead of costing
        // another round trip to the endpoint
        private Dictionary<string, string> _lastReply;

        private TMP_Text[] _rowLabels;
        private string _lastLoggedSignature;
        private int _targetIndex;
        private int _page;
        private bool _awaitingResponse;
        private float _requestTime;

        private int RowsPerPage => _rows != null ? _rows.Length : 0;
        private int PageCount => RowsPerPage == 0 ? 1 : Mathf.Max(1, Mathf.CeilToInt(_topics.Count / (float)RowsPerPage));
        private IRosTopicBinding ActiveBinding => _targetIndex < _bindings.Count ? _bindings[_targetIndex] : null;

        private void Awake()
        {
            _rowLabels = new TMP_Text[RowsPerPage];
            for (int i = 0; i < RowsPerPage; i++)
            {
                _rowLabels[i] = _rows[i].GetComponentInChildren<TMP_Text>();
                int rowIndex = i; // capture per row, not the shared loop variable
                _rows[i].onClick.AddListener(() => SelectRow(rowIndex));
            }
        }

        private void OnEnable()
        {
            // Both lists are snapshots: nodes come and go on the ROS side, and visualisations
            // come and go on this side (the Views page). Opening the tab is the moment to ask
            // about both.
            RebuildTargets();

            if (_refreshOnOpen)
                Refresh();
            else
                SetStatus("press Refresh to fetch the topic list");
        }

        private void Update()
        {
            if (!_awaitingResponse || Time.realtimeSinceStartup - _requestTime < _responseTimeout)
                return;

            _awaitingResponse = false;
            SetStatus("<color=#FF5252>no response</color> - is ROS connected?");
        }

        // Flips between "only topics this subscriber could actually take" and "everything the
        // endpoint advertises". Off by default because subscribing to a mismatched type just
        // produces deserialization errors - but when a topic you can see in `ros2 topic list`
        // isn't in this list, this is the button that tells you whether it is the filter hiding
        // it or the endpoint never reporting it at all.
        public void ToggleShowAllTypes()
        {
            _showAllTypes = !_showAllTypes;
            ApplyFilter();
        }

        public void Refresh()
        {
            _awaitingResponse = true;
            _requestTime = Time.realtimeSinceStartup;
            SetStatus("requesting topic list…");
            RenderTargetLabel();
            ROSConnection.GetOrCreateInstance().GetTopicAndTypeList(OnTopicList);
        }

        // Re-find every subscriber in the scene, keeping the one currently selected selected.
        // Public so the page can be told to look again without being closed and reopened.
        public void RebuildTargets()
        {
            IRosTopicBinding previous = ActiveBinding;

            _bindings.Clear();
            _bindingNames.Clear();

            var behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var found = new List<MonoBehaviour>();
            foreach (var behaviour in behaviours)
            {
                if (behaviour is IRosTopicBinding)
                    found.Add(behaviour);
            }

            // Hierarchy path, so a visualisation's own subscribers land together and in the same
            // order every time. FindObjectsByType's order is unspecified, and a target list that
            // reshuffles between openings is unusable with two arrow buttons.
            found.Sort((a, b) => string.Compare(HierarchyPath(a), HierarchyPath(b),
                System.StringComparison.Ordinal));

            foreach (var behaviour in found)
            {
                _bindings.Add((IRosTopicBinding)behaviour);
                _bindingNames.Add(behaviour.gameObject.name);
            }

            // Follow the selection rather than the index: adding a camera window renumbers
            // everything after it, and landing on a different subscriber than the one you were
            // looking at is how you retarget the wrong thing.
            int index = previous != null ? _bindings.IndexOf(previous) : -1;
            _targetIndex = index >= 0 ? index : 0;

            if (_bindings.Count == 0)
                Debug.LogWarning($"[XRViz] {nameof(TopicBrowserUI)} found no {nameof(IRosTopicBinding)} " +
                    "in the scene; topics will list but selecting one will do nothing.", this);
        }

        private static string HierarchyPath(Component component)
        {
            var path = new System.Text.StringBuilder(component.gameObject.name);
            for (Transform t = component.transform.parent; t != null; t = t.parent)
                path.Insert(0, t.name + "/");
            return path.ToString();
        }

        // Detach the selected subscriber from its topic: ROSConnection drops the callback and
        // nothing more arrives, which is the point - a subscriber left bound to a busy camera
        // costs socket bandwidth whether or not anyone is looking at it.
        //
        // SetTopic("") is the whole mechanism. It unsubscribes, stores the empty name, and
        // Subscribe() no-ops on it, which is exactly the state every visualisation in the
        // generated scene starts in - so a detached one is not a special case anywhere, it is
        // simply one that has not been given a topic yet.
        public void UnsubscribeTarget()
        {
            var binding = ActiveBinding;
            if (binding == null)
                return;

            string topic = binding.Topic;
            if (string.IsNullOrEmpty(topic))
            {
                SetStatus("<color=#FFB300>nothing bound to detach</color>");
                return;
            }

            binding.SetTopic(string.Empty);

            // Say what is left on the topic. Two windows on one camera is a normal thing to set
            // up here, and "did closing this one kill the other" is the question that follows.
            int others = RosSubscriberRegistry.CountOn(topic);
            SetStatus(others > 0
                ? $"detached from {topic} · {others} other subscriber(s) still on it"
                : $"detached from <color=#FFB300>{topic}</color>");

            RenderTargetLabel();
            Render();
        }

        public void NextTarget()
        {
            if (_bindings.Count == 0)
                return;
            _targetIndex = (_targetIndex + 1) % _bindings.Count;
            ApplyFilter();
        }

        public void PreviousTarget()
        {
            if (_bindings.Count == 0)
                return;
            _targetIndex = (_targetIndex - 1 + _bindings.Count) % _bindings.Count;
            ApplyFilter();
        }

        public void NextPage()
        {
            if (_page + 1 >= PageCount)
                return;
            _page++;
            Render();
        }

        public void PreviousPage()
        {
            if (_page == 0)
                return;
            _page--;
            Render();
        }

        public void ToggleVisibility()
        {
            gameObject.SetActive(!gameObject.activeSelf);
        }

        // Runs on the main thread: ROSConnection dispatches sys-command replies from its Update,
        // not the socket thread, so touching UI straight from here is safe
        private void OnTopicList(Dictionary<string, string> topicsAndTypes)
        {
            _awaitingResponse = false;
            _lastReply = topicsAndTypes;
            LogReply(topicsAndTypes);
            ApplyFilter();
        }

        // The whole reply, names and exact type strings, whenever it changes from the last one
        // logged. The panel can only ever show the topics that got past the type filter, so when
        // a topic is missing this is the only place the truth is written down: either it is in
        // here (and the filter hid it) or it is not (and the endpoint never saw it). Logged on
        // change rather than on every fetch, so opening the tab repeatedly doesn't spam.
        private void LogReply(Dictionary<string, string> reply)
        {
            if (reply == null)
                return;

            string signature = string.Join("|", reply.Select(kv => kv.Key + "=" + kv.Value).OrderBy(t => t));
            if (signature == _lastLoggedSignature)
                return;
            _lastLoggedSignature = signature;

            Debug.Log($"[XRViz] ros_tcp_endpoint advertises {reply.Count} topic(s):\n" +
                string.Join("\n", reply.OrderBy(kv => kv.Key).Select(kv => $"  {kv.Key}  ({kv.Value})")));
        }

        private void ApplyFilter()
        {
            RenderTargetLabel();
            RenderTypeFilterLabel();

            if (_lastReply == null)
            {
                // Switched target before any reply arrived - nothing cached to re-filter
                Refresh();
                return;
            }

            var binding = ActiveBinding;
            string wanted = binding != null ? NormalizeMessageName(binding.RosMessageName) : null;

            _topics.Clear();
            _topics.AddRange(_lastReply
                .Where(kv => _showAllTypes || wanted == null || NormalizeMessageName(kv.Value) == wanted)
                .Select(kv => kv.Key)
                .OrderBy(t => t));

            _page = 0;
            Render();

            if (_showAllTypes)
            {
                SetStatus($"{_topics.Count} topics \u00b7 <color=#FFB300>all types</color>");
            }
            else if (_topics.Count > 0)
            {
                SetStatus($"{_topics.Count} of {_lastReply.Count} topics match");
            }
            else if (_lastReply.Count == 0)
            {
                SetStatus("no topics advertised");
            }
            else
            {
                SetStatus($"<color=#FFB300>0 of {_lastReply.Count} match {ShortTypeName(wanted)}</color>");
                // The panel is far too small to list 40 types, and this is exactly the moment
                // you need to see them - so put them where the Editor console and adb logcat
                // will pick them up
                Debug.Log($"[XRViz] No topic matched '{wanted}'. The endpoint advertises:\n" +
                    string.Join("\n", _lastReply.Select(kv => $"  {kv.Key}  ({kv.Value})")));
            }
        }

        // ROS 1 names a type "sensor_msgs/LaserScan"; ROS 2 names it "sensor_msgs/msg/LaserScan",
        // and ros_tcp_endpoint passes whichever it sees straight through untouched. Unity's
        // MessageRegistry only ever knows the ROS 1 form, so both sides have to have the
        // interface-kind infix stripped before comparing - otherwise a ROS 2 endpoint matches
        // nothing at all and the list comes back silently empty.
        private static string NormalizeMessageName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return name
                .Replace("/msg/", "/")
                .Replace("/srv/", "/")
                .Replace("/action/", "/");
        }

        private void SelectRow(int rowIndex)
        {
            var binding = ActiveBinding;
            int topicIndex = _page * RowsPerPage + rowIndex;
            if (binding == null || topicIndex >= _topics.Count)
                return;

            string topic = _topics[topicIndex];
            binding.SetTopic(topic);
            SetStatus($"subscribed to <color=#4CAF50>{topic}</color>");
            RenderTargetLabel();
            Render(); // move the ▶ marker onto the new selection
        }

        private void RenderTypeFilterLabel()
        {
            if (_typeFilterLabel == null)
                return;
            _typeFilterLabel.text = _showAllTypes ? "Types: all" : "Types: matching";
        }

        private void RenderTargetLabel()
        {
            if (_targetLabel == null)
                return;

            var binding = ActiveBinding;
            if (binding == null)
            {
                _targetLabel.text = "<no target>";
                return;
            }

            string topic = string.IsNullOrEmpty(binding.Topic) ? "<none>" : binding.Topic;
            string name = _targetIndex < _bindingNames.Count ? _bindingNames[_targetIndex] : "?";
            _targetLabel.text = $"{_targetIndex + 1}/{_bindings.Count}  <b>{name}</b>" +
                $"\n<size=80%>{ShortTypeName(binding.RosMessageName)} · {topic}</size>";
        }

        // "sensor_msgs/JointState" -> "JointState"; the package prefix is just noise in a label
        // this narrow, and the full name is already in the panel title area
        private static string ShortTypeName(string rosMessageName)
        {
            if (string.IsNullOrEmpty(rosMessageName))
                return "?";
            int slash = rosMessageName.LastIndexOf('/');
            return slash >= 0 && slash < rosMessageName.Length - 1
                ? rosMessageName.Substring(slash + 1)
                : rosMessageName;
        }

        private void Render()
        {
            if (_pageLabel != null)
                _pageLabel.text = $"{_page + 1} / {PageCount}";

            var binding = ActiveBinding;
            string current = binding != null ? binding.Topic : null;

            for (int i = 0; i < RowsPerPage; i++)
            {
                int topicIndex = _page * RowsPerPage + i;
                bool hasTopic = topicIndex < _topics.Count;

                // Leave empty rows in place but non-interactable, so the page doesn't reflow
                // and there's nothing to click into
                _rows[i].interactable = hasTopic;
                if (_rowLabels[i] == null)
                    continue;

                if (!hasTopic)
                {
                    _rowLabels[i].text = "";
                    continue;
                }

                string topic = _topics[topicIndex];
                _rowLabels[i].text = topic == current ? $"<color=#4CAF50>▶ {topic}</color>" : topic;
            }
        }

        private void SetStatus(string text)
        {
            if (_status != null)
                _status.text = text;
        }
    }
}
