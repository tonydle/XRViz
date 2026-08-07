using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // Lists the topics the ros_tcp_endpoint is advertising and lets one be picked from inside the
    // headset, rebinding a subscriber live. Paged rather than scrolled: a fixed set of row
    // buttons is far easier to hit with a ray than a ScrollRect, and needs no mask or layout
    // group. Rows are recycled - the labels are rewritten per page.
    public class TopicBrowserUI : MonoBehaviour
    {
        // Must implement IRosTopicBinding. Serialized as MonoBehaviour because Unity can't
        // serialize an interface reference directly; validated in Awake.
        [SerializeField] private MonoBehaviour _target;
        [SerializeField] private TMP_Text _title;
        [SerializeField] private TMP_Text _status;
        [SerializeField] private TMP_Text _pageLabel;
        [SerializeField] private Button[] _rows;

        // GetTopicAndTypeList has no failure path - if the endpoint isn't connected the request
        // just vanishes and the callback never fires. Without our own deadline the panel would
        // sit on "requesting…" forever with no way to tell that apart from a slow reply.
        [SerializeField] private float _responseTimeout = 5f;

        // Show every topic rather than only those matching the subscriber's message type.
        // Off by default: subscribing to a mismatched type gets you deserialization errors.
        [SerializeField] private bool _showAllTypes = false;

        private IRosTopicBinding _binding;
        private readonly List<string> _topics = new List<string>();
        private TMP_Text[] _rowLabels;
        private int _page;
        private int _totalTopicCount;
        private bool _awaitingResponse;
        private float _requestTime;

        private int RowsPerPage => _rows != null ? _rows.Length : 0;
        private int PageCount => RowsPerPage == 0 ? 1 : Mathf.Max(1, Mathf.CeilToInt(_topics.Count / (float)RowsPerPage));

        private void Awake()
        {
            _binding = _target as IRosTopicBinding;
            if (_binding == null)
                Debug.LogWarning($"[XRViz] {nameof(TopicBrowserUI)} has no target implementing " +
                    $"{nameof(IRosTopicBinding)}; topics will list but selecting one will do nothing.", this);

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
            // Opening the panel is the natural moment to ask - the list is a snapshot, and
            // nodes come and go while the app is running
            Refresh();
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
            if (_title != null)
                _title.text = _binding == null
                    ? "Topics"
                    : $"Topics <size=70%>({_binding.RosMessageName})</size>";

            _awaitingResponse = true;
            _requestTime = Time.realtimeSinceStartup;
            SetStatus("requesting topic list…");
            ROSConnection.GetOrCreateInstance().GetTopicAndTypeList(OnTopicList);
        }

        // Runs on the main thread: ROSConnection dispatches sys-command replies from its Update,
        // not the socket thread, so touching UI straight from here is safe
        private void OnTopicList(Dictionary<string, string> topicsAndTypes)
        {
            _awaitingResponse = false;
            _totalTopicCount = topicsAndTypes.Count;

            string wanted = _binding != null ? _binding.RosMessageName : null;
            _topics.Clear();
            _topics.AddRange(topicsAndTypes
                .Where(kv => _showAllTypes || wanted == null || kv.Value == wanted)
                .Select(kv => kv.Key)
                .OrderBy(t => t));

            _page = 0;
            Render();

            if (_topics.Count == 0)
                SetStatus(_totalTopicCount == 0
                    ? "no topics advertised"
                    : $"<color=#FFB300>none of {_totalTopicCount} topics match</color>");
            else
                SetStatus($"{_topics.Count} of {_totalTopicCount} topics match");
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

        private void SelectRow(int rowIndex)
        {
            int topicIndex = _page * RowsPerPage + rowIndex;
            if (_binding == null || topicIndex >= _topics.Count)
                return;

            string topic = _topics[topicIndex];
            _binding.SetTopic(topic);
            SetStatus($"subscribed to <color=#4CAF50>{topic}</color>");
            Render(); // move the ▶ marker onto the new selection
        }

        private void Render()
        {
            if (_pageLabel != null)
                _pageLabel.text = $"{_page + 1} / {PageCount}";

            string current = _binding != null ? _binding.Topic : null;
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
