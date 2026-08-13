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
    // Paged rather than scrolled: a fixed set of row buttons is far easier to hit with a ray than
    // a ScrollRect, and needs no mask or layout group. Rows are recycled - labels are rewritten
    // per page.
    public class TopicBrowserUI : MonoBehaviour
    {
        // Each must implement IRosTopicBinding. Serialized as MonoBehaviour because Unity can't
        // serialize interface references directly; validated in Awake.
        [SerializeField] private MonoBehaviour[] _targets;
        [SerializeField] private TMP_Text _title;
        [SerializeField] private TMP_Text _targetLabel;
        [SerializeField] private TMP_Text _status;
        [SerializeField] private TMP_Text _pageLabel;
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
        private int _targetIndex;
        private int _page;
        private bool _awaitingResponse;
        private float _requestTime;

        private int RowsPerPage => _rows != null ? _rows.Length : 0;
        private int PageCount => RowsPerPage == 0 ? 1 : Mathf.Max(1, Mathf.CeilToInt(_topics.Count / (float)RowsPerPage));
        private IRosTopicBinding ActiveBinding => _targetIndex < _bindings.Count ? _bindings[_targetIndex] : null;

        private void Awake()
        {
            if (_targets != null)
            {
                foreach (var target in _targets)
                {
                    if (target is IRosTopicBinding binding)
                    {
                        _bindings.Add(binding);
                        _bindingNames.Add(target.gameObject.name);
                    }
                    else if (target != null)
                        Debug.LogWarning($"[XRViz] {target.GetType().Name} on '{target.name}' does not " +
                            $"implement {nameof(IRosTopicBinding)}; skipping it in the topic browser.", this);
                }
            }

            if (_bindings.Count == 0)
                Debug.LogWarning($"[XRViz] {nameof(TopicBrowserUI)} has no usable targets; topics will " +
                    "list but selecting one will do nothing.", this);

            _rowLabels = new TMP_Text[RowsPerPage];
            for (int i = 0; i < RowsPerPage; i++)
            {
                _rowLabels[i] = _rows[i].GetComponentInChildren<TMP_Text>();
                int rowIndex = i; // capture per row, not the shared loop variable
                _rows[i].onClick.AddListener(() => SelectRow(rowIndex));
            }

            if (_title != null)
                _title.text = "ROS Topics";
        }

        private void OnEnable()
        {
            // Opening the panel is the natural moment to ask - the list is a snapshot, and
            // nodes come and go while the app is running
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

        public void Refresh()
        {
            _awaitingResponse = true;
            _requestTime = Time.realtimeSinceStartup;
            SetStatus("requesting topic list…");
            RenderTargetLabel();
            ROSConnection.GetOrCreateInstance().GetTopicAndTypeList(OnTopicList);
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
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            RenderTargetLabel();

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

            if (_topics.Count > 0)
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
