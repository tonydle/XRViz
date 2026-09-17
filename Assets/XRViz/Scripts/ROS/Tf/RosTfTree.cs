using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosTFMessage = RosMessageTypes.Tf2.TFMessageMsg;

namespace Unity.Robotics
{
    // The scene's TF tree: /tf and /tf_static collected into one frame table, resolved on demand
    // into Unity-space poses. THIS GAMEOBJECT'S TRANSFORM IS THE FIXED FRAME - every pose this
    // reports in world space is measured out from here, so moving this object (it carries a
    // PlacementHandle of its own) carries the whole TF world with it.
    //
    // Why not the package's TFSystem, which does most of this already: it keeps a SEPARATE frame
    // table per TF topic. Anything published on /tf_static therefore lands in a table that knows
    // nothing about the /tf one, and a chain that crosses the two - which is the normal case, a
    // static sensor mount under a moving arm - cannot be resolved at all. It also spawns a
    // GameObject per frame, which is a lot of scene for data nobody looks at directly.
    //
    // Frame names are stored with any leading '/' stripped (ROS 1 wrote "/base_link", ROS 2 writes
    // "base_link", and a rig bridging the two publishes both for the same frame).
    [DefaultExecutionOrder(-200)]
    public class RosTfTree : MonoBehaviour
    {
        // First one to Awake wins. There is no sane meaning to two TF worlds in one scene, and a
        // second would silently steal half the anchors.
        public static RosTfTree Instance { get; private set; }

        [SerializeField] private string _tfTopic = "/tf";

        // Static transforms are latched on the ROS side, and ros_tcp_endpoint only subscribes when
        // we ask - so anything published before this scene connected is simply missed. If a static
        // link never shows up, restart the node that publishes it (or republish it) after connecting.
        [SerializeField] private string _tfStaticTopic = "/tf_static";

        // Which frame poses are measured from - i.e. what this GameObject's Transform stands for.
        // Left empty each chain is resolved to its own root (usually "world" or "odom"), which is
        // right often enough to be the default and needs no configuration.
        [SerializeField] private string _fixedFrame = "";

        // Drop a frame that has stopped updating, so an anchor freezes visibly rather than
        // drifting on a pose from a minute ago. Static frames are exempt - never republished by
        // design. 0 disables the check.
        [SerializeField] private float _staleAfterSeconds = 0f;

        // A malformed tree (a frame that is its own ancestor) would spin the walk forever
        private const int k_MaxChainDepth = 64;

        private class Frame
        {
            public string Parent;
            public Vector3 Position;
            public Quaternion Rotation;
            public float LastUpdate;
            public bool IsStatic;
        }

        // child frame name -> its pose in its parent. A frame that is only ever a parent (the tree
        // root) has no entry here, which is what terminates the walk - hence _known alongside, so
        // "is this a frame at all?" can still be answered for roots.
        private readonly Dictionary<string, Frame> _frames = new Dictionary<string, Frame>();
        private readonly HashSet<string> _known = new HashSet<string>();

        // ROSConnection dispatches subscriber callbacks from its own Update, so these arrive on
        // the main thread today. Queued anyway: it costs nothing at TF rates, and it means the
        // table is only ever mutated at one known point in the frame rather than mid-resolve.
        // The flag rides along with the message because both topics share the queue and only the
        // static ones are exempt from the staleness check.
        private readonly ConcurrentQueue<(RosTFMessage Message, bool IsStatic)> _incoming =
            new ConcurrentQueue<(RosTFMessage, bool)>();

        private float _lastMessageRealtime = -1f;
        private float _lastStaticMessageRealtime = -1f;
        private int _messageCount;
        private int _staticMessageCount;
        private bool _cycleLogged;

        public string FixedFrame => _fixedFrame;
        public int FrameCount => _known.Count;
        public bool HasFrames => _known.Count > 0;

        // Time.realtimeSinceStartup of the last TF message, or -1 if none has arrived
        public float LastMessageRealtime => _lastMessageRealtime;

        // The two topics are worth telling apart when nothing resolves: /tf_static is LATCHED on
        // the ROS side and ros_tcp_endpoint only subscribes when asked, so anything published
        // before this scene connected is simply never seen - and a chain that crosses into a
        // static link then cannot be walked, however healthy /tf looks.
        public float LastStaticMessageRealtime => _lastStaticMessageRealtime;
        public int MessageCount => _messageCount;
        public int StaticMessageCount => _staticMessageCount;
        public string TfTopic => _tfTopic;
        public string TfStaticTopic => _tfStaticTopic;

        // One frame's link to its parent, for diagnostics. False when the frame is unknown or is
        // a tree root (a root is known but has no entry of its own - that is what ends the walk).
        public bool TryDescribeFrame(string frameId, out string parent, out bool isStatic,
            out float ageSeconds)
        {
            parent = null;
            isStatic = false;
            ageSeconds = -1f;

            if (string.IsNullOrEmpty(frameId) || !_frames.TryGetValue(Normalize(frameId), out var frame))
                return false;

            parent = frame.Parent;
            isStatic = frame.IsStatic;
            ageSeconds = Time.realtimeSinceStartup - frame.LastUpdate;
            return true;
        }

        public IEnumerable<string> KnownFrames => _known;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[XRViz] A second {nameof(RosTfTree)} on '{name}' is being ignored; " +
                    $"'{Instance.name}' already owns the TF world.", this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            var ros = ROSConnection.GetOrCreateInstance();
            if (!string.IsNullOrEmpty(_tfTopic))
                ros.Subscribe<RosTFMessage>(_tfTopic, Receive);

            // Separate subscription, same table - that difference from TFSystem is the whole point
            if (!string.IsNullOrEmpty(_tfStaticTopic) && _tfStaticTopic != _tfTopic)
                ros.Subscribe<RosTFMessage>(_tfStaticTopic, ReceiveStatic);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Receive(RosTFMessage message)
        {
            _incoming.Enqueue((message, false));
        }

        private void ReceiveStatic(RosTFMessage message)
        {
            _incoming.Enqueue((message, true));
        }

        private void Update()
        {
            while (_incoming.TryDequeue(out var entry))
                Apply(entry.Message, entry.IsStatic);
        }

        private void Apply(RosTFMessage message, bool isStatic)
        {
            if (message?.transforms == null)
                return;

            float now = Time.realtimeSinceStartup;
            foreach (var stamped in message.transforms)
            {
                string child = Normalize(stamped.child_frame_id);
                string parent = Normalize(stamped.header.frame_id);
                if (string.IsNullOrEmpty(child))
                    continue;

                if (!_frames.TryGetValue(child, out var frame))
                {
                    frame = new Frame();
                    _frames[child] = frame;
                }

                frame.Parent = parent;
                // ROS is right-handed Z-up and Unity left-handed Y-up; From<FLU> is the conversion
                // the rest of the project uses (CLAUDE.md), applied here to the transform between
                // frames rather than to any data expressed in them
                frame.Position = stamped.transform.translation.From<FLU>();
                frame.Rotation = stamped.transform.rotation.From<FLU>();
                frame.LastUpdate = now;
                // Sticky: a frame that has ever arrived on /tf_static stays exempt from the
                // staleness check even if something later republishes it on /tf
                frame.IsStatic |= isStatic;

                _known.Add(child);
                if (!string.IsNullOrEmpty(parent))
                    _known.Add(parent);
            }

            _lastMessageRealtime = now;
            _messageCount++;
            if (isStatic)
            {
                _lastStaticMessageRealtime = now;
                _staticMessageCount++;
            }
        }

        public bool HasFrame(string frameId)
        {
            return !string.IsNullOrEmpty(frameId) && _known.Contains(Normalize(frameId));
        }

        // Pose of frameId in fixed-frame space (Unity axes). False when the frame is unknown, has
        // gone stale, or sits in a different tree from the fixed frame - all of which mean "don't
        // move anything", never "move it to the origin".
        public bool TryGetPose(string frameId, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (string.IsNullOrEmpty(frameId))
                return false;

            if (!TryGetRootPose(Normalize(frameId), out var framePos, out var frameRot, out string frameRoot))
                return false;

            if (string.IsNullOrEmpty(_fixedFrame))
            {
                position = framePos;
                rotation = frameRot;
                return true;
            }

            if (!TryGetRootPose(Normalize(_fixedFrame), out var fixedPos, out var fixedRot, out string fixedRoot))
                return false;

            // Two frames under different roots have no known relationship - composing them anyway
            // would put the visualisation somewhere confidently wrong
            if (frameRoot != fixedRoot)
                return false;

            Quaternion inverse = Quaternion.Inverse(fixedRot);
            position = inverse * (framePos - fixedPos);
            rotation = inverse * frameRot;
            return true;
        }

        // Same, placed out from this GameObject's Transform - which is what the fixed frame
        // physically means in the room
        public bool TryGetWorldPose(string frameId, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (!TryGetPose(frameId, out var local, out var localRotation))
                return false;

            position = transform.TransformPoint(local);
            rotation = transform.rotation * localRotation;
            return true;
        }

        // Walks child -> parent to the top of the chain, composing as it goes, and reports which
        // root it ended at so two frames can be checked for being in the same tree.
        private bool TryGetRootPose(string frameId, out Vector3 position, out Quaternion rotation,
            out string root)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            root = frameId;

            if (!_known.Contains(frameId))
                return false;

            float now = Time.realtimeSinceStartup;
            string name = frameId;
            int depth = 0;

            while (_frames.TryGetValue(name, out var frame))
            {
                if (++depth > k_MaxChainDepth)
                {
                    if (!_cycleLogged)
                    {
                        _cycleLogged = true;
                        Debug.LogError($"[XRViz] TF chain from '{frameId}' is longer than {k_MaxChainDepth} " +
                            "links or contains a cycle; giving up on it.", this);
                    }
                    return false;
                }

                if (_staleAfterSeconds > 0f && !frame.IsStatic &&
                    now - frame.LastUpdate > _staleAfterSeconds)
                    return false;

                // Express the accumulated pose one level up: p_parent = R * p_child + t
                position = frame.Rotation * position + frame.Position;
                rotation = frame.Rotation * rotation;
                name = frame.Parent;

                if (string.IsNullOrEmpty(name))
                    break;
            }

            root = name;
            return true;
        }

        private static string Normalize(string frameId)
        {
            if (string.IsNullOrEmpty(frameId))
                return frameId;
            return frameId[0] == '/' ? frameId.Substring(1) : frameId;
        }
    }
}
