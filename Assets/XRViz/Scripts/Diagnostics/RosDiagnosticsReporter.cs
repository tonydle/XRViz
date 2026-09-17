using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Unity.Robotics
{
    // Writes the whole ROS-side state of the scene to the log on a timer, so "nothing is drawing
    // and I don't know why" can be answered from `adb logcat` without wearing the headset.
    //
    // It exists because neither of the two things you would want to read is reachable otherwise:
    // the app does not connect to ROS on its own (ROSConnectionPrefab has ConnectOnStart off, by
    // design - you press Connect on the panel), and the topic list is only fetched when the
    // Topics tab is opened. Launch the APK over adb with nobody in the headset and both of those
    // never happen, so the log says nothing at all about ROS. This component does both on its own.
    //
    // Everything it reports is per SUBSCRIBER, never per expected topic name. XRViz binds any
    // compatible topic to any subscriber at runtime and nothing in it may assume a topic is
    // called the conventional thing - so the question answered here is "what is this subscriber
    // bound to and what has reached it", which is the same question whatever the topic is called.
    [DefaultExecutionOrder(200)]
    public class RosDiagnosticsReporter : MonoBehaviour
    {
        [Tooltip("Connect to ROS at startup. The app otherwise waits for the panel's Connect " +
                 "button, which nobody can press when it is launched over adb. Turn this off to " +
                 "keep the manual-connect behaviour and still get the reports.")]
        [SerializeField] private bool _connectOnStart = true;

        [Tooltip("Seconds between reports. Each is a handful of lines; at 5 s this is readable " +
                 "in a live `adb logcat` without drowning everything else.")]
        [SerializeField] private float _reportSeconds = 5f;

        [Tooltip("Ask the endpoint for its topic list at startup and log the lot - names and " +
                 "exact type strings. This is the only place the difference between 'the endpoint " +
                 "never saw it' and 'the type filter hid it' is written down.")]
        [SerializeField] private bool _logTopicList = true;

        [Tooltip("Repeat the topic list on every report rather than only when it changes. Off is " +
                 "usually right - the list is stable and the per-subscriber table is the part " +
                 "that moves.")]
        [SerializeField] private bool _repeatTopicList = false;

        [Tooltip("Also report the TF tree and every TfAnchor: whether /tf and /tf_static are " +
                 "arriving, which frames are known, and what each anchor is doing with them.")]
        [SerializeField] private bool _reportTf = true;

        [Tooltip("Most frame names to list. The tree is printed parent-first so a second, " +
                 "disconnected tree is obvious - which is the usual reason a frame that exists " +
                 "still will not resolve.")]
        [SerializeField] private int _maxFramesListed = 40;

        [Tooltip("Stop reporting once every subscriber that has a topic is receiving. Leaves the " +
                 "log quiet when the scene is healthy.")]
        [SerializeField] private bool _stopWhenHealthy = false;

        private ROSConnection _ros;
        private float _nextReport;
        private string _lastTopicListSignature;
        private bool _topicListRequested;

        private void Start()
        {
            _ros = ROSConnection.GetOrCreateInstance();

            if (_connectOnStart)
            {
                Debug.Log($"[XRViz] Diagnostics: connecting to {_ros.RosIPAddress}:{_ros.RosPort}");
                _ros.Connect();
            }

            // First report straight away, so a launch that dies early still says something
            _nextReport = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextReport)
                return;
            _nextReport = Time.unscaledTime + Mathf.Max(1f, _reportSeconds);

            if (_logTopicList && (!_topicListRequested || _repeatTopicList))
            {
                _topicListRequested = true;
                _ros.GetTopicAndTypeList(OnTopicList);
            }

            Report();
        }

        private void OnTopicList(Dictionary<string, string> topicsAndTypes)
        {
            if (topicsAndTypes == null)
                return;

            string signature = string.Join("|",
                topicsAndTypes.Select(kv => kv.Key + "=" + kv.Value).OrderBy(t => t));
            if (signature == _lastTopicListSignature)
                return;
            _lastTopicListSignature = signature;

            var text = new StringBuilder();
            text.Append("[XRViz] Diagnostics: endpoint advertises ")
                .Append(topicsAndTypes.Count).Append(" topic(s)");

            // Grouped by type, because the question this answers is nearly always "what could
            // this subscriber legally be pointed at", and that is a question about types
            foreach (var group in topicsAndTypes.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
            {
                text.Append("\n  ").Append(group.Key);
                foreach (var topic in group.Select(kv => kv.Key).OrderBy(t => t))
                    text.Append("\n      ").Append(topic);
            }

            Debug.Log(text.ToString());
        }

        private void Report()
        {
            var text = new StringBuilder();
            text.Append("[XRViz] Diagnostics ").Append(DescribeConnection());

            var subscribers = FindObjectsByType<MonoBehaviour>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OfType<IRosSubscriberDiagnostics>()
                .OrderBy(sub => ((MonoBehaviour)sub).gameObject.name)
                .ToList();

            if (subscribers.Count == 0)
            {
                text.Append("\n  no subscribers in the scene");
                Debug.Log(text.ToString());
                return;
            }

            bool allHealthy = true;
            foreach (var sub in subscribers)
            {
                var behaviour = (MonoBehaviour)sub;
                string state = DescribeSubscriber(sub, behaviour, out bool healthy);
                allHealthy &= healthy;

                text.Append("\n  ").Append(behaviour.gameObject.name.PadRight(22))
                    .Append(ShortType(sub.RosMessageName).PadRight(16))
                    .Append(string.IsNullOrEmpty(sub.Topic) ? "<no topic>" : sub.Topic)
                    .Append("  ").Append(state);
            }

            Debug.Log(text.ToString());

            if (_reportTf)
                ReportTf();

            if (_stopWhenHealthy && allHealthy)
            {
                Debug.Log("[XRViz] Diagnostics: everything bound is receiving; going quiet.");
                enabled = false;
            }
        }

        // TF is reported separately because RosTfTree is not a RosSubscriber - it subscribes to
        // /tf and /tf_static directly - so none of it shows up in the table above. Which meant
        // the one question the table could not answer was "is TF arriving at all".
        private void ReportTf()
        {
            var text = new StringBuilder();
            var tree = RosTfTree.Instance;

            if (tree == null)
            {
                Debug.Log("[XRViz] Diagnostics TF - NO RosTfTree IN THE SCENE, so nothing can be " +
                    "placed from /tf at all");
                return;
            }

            text.Append("[XRViz] Diagnostics TF - ").Append(tree.name).Append(": ");
            text.Append(DescribeTopic(tree.TfTopic, tree.MessageCount, tree.LastMessageRealtime));
            text.Append(", ");
            text.Append(DescribeTopic(tree.TfStaticTopic, tree.StaticMessageCount,
                tree.LastStaticMessageRealtime));
            text.Append(", ").Append(tree.FrameCount).Append(" frames, fixed frame: ")
                .Append(string.IsNullOrEmpty(tree.FixedFrame) ? "each chain's root" : tree.FixedFrame);

            // child <- parent, so a second disconnected tree stands out: every frame whose parent
            // is not itself a known child is a root, and two roots means two worlds
            int listed = 0;
            foreach (string frame in tree.KnownFrames)
            {
                if (listed++ >= Mathf.Max(1, _maxFramesListed))
                {
                    text.Append("\n    ... ").Append(tree.FrameCount - listed + 1).Append(" more");
                    break;
                }

                text.Append("\n    ").Append(frame);
                if (tree.TryDescribeFrame(frame, out string parent, out bool isStatic, out float age))
                {
                    text.Append("  <- ").Append(string.IsNullOrEmpty(parent) ? "<root>" : parent);
                    text.Append(isStatic ? "  (static)" : $"  ({age:0.0} s ago)");
                }
                else
                {
                    text.Append("  <- <root, never published as a child>");
                }
            }

            var anchors = FindObjectsByType<TfAnchor>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).OrderBy(a => a.Label).ToList();

            text.Append("\n  anchors:");
            if (anchors.Count == 0)
                text.Append("\n    none in the scene");

            foreach (var anchor in anchors)
            {
                text.Append("\n    ").Append(anchor.Label.PadRight(16))
                    .Append((string.IsNullOrEmpty(anchor.FrameId) ? "<no frame>" : anchor.FrameId).PadRight(32))
                    .Append(DescribeAnchor(anchor));
            }

            Debug.Log(text.ToString());
        }

        private static string DescribeTopic(string topic, int count, float lastRealtime)
        {
            if (string.IsNullOrEmpty(topic))
                return "<not subscribed>";
            if (count == 0)
                return $"{topic} NOTHING RECEIVED";
            return $"{topic} {count} msgs, {Time.realtimeSinceStartup - lastRealtime:0.0} s ago";
        }

        // Each of these is a different fix, and "not in /tf" versus "in a different tree" is the
        // distinction that matters most - the second one looks identical from the panel.
        private static string DescribeAnchor(TfAnchor anchor)
        {
            switch (anchor.Status)
            {
                case TfAnchor.AnchorStatus.Anchored:
                    return "ANCHORED";
                case TfAnchor.AnchorStatus.Manual:
                    return "MANUAL - nothing has switched this to TF (panel: TF tab, All -> TF)";
                case TfAnchor.AnchorStatus.NoFrameId:
                    return "NO FRAME YET - no message has arrived to say which frame its data is in";
                case TfAnchor.AnchorStatus.FrameNotInTf:
                    return "FRAME NOT IN /tf - unknown, stale, or in a different tree from the fixed frame";
                default:
                    return "NO TF TREE";
            }
        }

        // The four distinguishable states, each of which has a different fix. Anything past the
        // last one is downstream of the subscriber - a visualiser, a shader, or where the thing
        // is placed - and this component deliberately does not guess about those.
        private static string DescribeSubscriber(IRosSubscriberDiagnostics sub,
            MonoBehaviour behaviour, out bool healthy)
        {
            healthy = false;

            if (!behaviour.isActiveAndEnabled)
                return "OBJECT HIDDEN - shown from the panel's Scene tab";

            if (string.IsNullOrEmpty(sub.Topic))
                return "NO TOPIC BOUND - pick one on the Topics tab";

            if (!sub.Subscribed)
                return "BOUND BUT NOT SUBSCRIBED - the pick never reached ROSConnection";

            if (sub.MessagesReceived == 0)
                return "0 MESSAGES - nothing is publishing this topic, or not with this type";

            float age = Time.realtimeSinceStartup - sub.LastMessageRealtime;
            if (age > 5f)
                return $"STALE - {sub.MessagesReceived} msgs, last {age:0.0} s ago";

            healthy = true;
            return $"receiving - {sub.MessagesReceived} msgs, last {age:0.0} s ago";
        }

        private string DescribeConnection()
        {
            if (_ros == null)
                return "- no ROSConnection";

            string state;
            if (!_ros.HasConnectionThread)
                state = "connecting";
            else if (_ros.HasConnectionError)
                state = "ERROR";
            else
                state = "ok";

            float last = _ros.LastMessageReceivedRealtime;
            string lastText = last <= 0f
                ? "no messages yet"
                : $"last message {Time.realtimeSinceStartup - last:0.0} s ago";

            return $"- {_ros.RosIPAddress}:{_ros.RosPort} {state}, {lastText}";
        }

        private static string ShortType(string rosMessageName)
        {
            if (string.IsNullOrEmpty(rosMessageName))
                return "?";
            int slash = rosMessageName.LastIndexOf('/');
            return slash >= 0 && slash < rosMessageName.Length - 1
                ? rosMessageName.Substring(slash + 1)
                : rosMessageName;
        }
    }
}
