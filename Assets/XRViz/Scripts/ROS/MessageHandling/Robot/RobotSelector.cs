using System;
using System.Linq;
using UnityEngine;

namespace Unity.Robotics
{
    // Which robot the scene shows, chosen from the headset. One at a time: the arm the chassis
    // calibration snaps onto has to be unambiguous, and two robots standing in the room would be
    // two answers to "where is the robot?".
    //
    // Exactly one robot INSTANCE exists, rather than one per option with the unused ones
    // deactivated. Everything that finds things at runtime - the Topics browser, the TF and Scene
    // pages, All to TF, the diagnostics log - sweeps with FindObjectsInactive.Include, so a
    // deactivated robot would still turn up as a subscriber, an anchor and a Scene row, and All
    // to TF would suspend the shared handle on its behalf. Destroying the old one and
    // instantiating the new one keeps every one of those sweeps seeing one robot.
    //
    // Every topic under the robot is namespaced to it: the prefab's /joint_states becomes
    // /<namespace>/joint_states. That happens before the new robot's Start, while SetTopic is
    // still plain configuration, so it never subscribes to the un-namespaced topic at all.
    public class RobotSelector : MonoBehaviour
    {
        [Serializable]
        public class RobotOption
        {
            [Tooltip("What the panel calls it")]
            public string Label;

            public GameObject Prefab;

            [Tooltip("ROS namespace its topics are moved under. Blank uses the prefab's name, which " +
                     "for a URDF import is the URDF's robot name.")]
            public string Namespace;

            [Tooltip("The TF frame the robot is placed by when TF-anchored")]
            public string RootFrameId = "base_link";

            [Tooltip("Off: the grab handle goes on the top-front-left corner of the robot's " +
                     "bounds. On: it goes at Handle Position instead.")]
            public bool UseHandlePosition;

            [Tooltip("Where the grab handle sits relative to the robot's root link, in that " +
                     "link's yaw frame")]
            public Vector3 HandlePosition;
        }

        [SerializeField] private RobotOption[] _robots = new RobotOption[0];

        // Index into _robots of the robot showing now. The scene generator places that one as a
        // prefab instance and serializes it below, so the scene shows a robot in the Editor too;
        // with none serialized, Awake spawns it.
        [SerializeField] private int _selected;
        [SerializeField] private GameObject _current;

        // The amber handle. One handle for whichever robot is showing - retargeted on a switch
        // rather than made and destroyed, so Reset Layout and the Guide's colour key stay right.
        [SerializeField] private PlacementHandle _handle;

        // Hold references to the robot that a switch has to repoint
        [SerializeField] private ArucoRobotCalibrator _calibrator;
        [SerializeField] private RosConnectionStatusUI _status;

        // Clear of the robot's bounds so the sphere sits beside the corner rather than inside it
        const float k_HandleClearance = 0.04f;

        // Same name as RobotStateWriterController uses: never part of what the robot looks like
        const string k_CollisionsObjectName = "Collisions";

        private TfAnchor _anchor;
        private RosSubscriberJointState _jointStateSub;

        // Raised after a switch, for anything showing which robot this is
        public event Action Changed;

        public int Count => _robots != null ? _robots.Length : 0;
        public int Selected => _selected;
        public GameObject Current => _current;

        public string LabelOf(int index) => IsValid(index) ? _robots[index].Label : string.Empty;

        public string NamespaceOf(int index)
        {
            if (!IsValid(index))
                return string.Empty;

            var option = _robots[index];
            string ns = string.IsNullOrWhiteSpace(option.Namespace)
                ? (option.Prefab != null ? option.Prefab.name : string.Empty)
                : option.Namespace;
            return ns.Trim().Trim('/');
        }

        // What the robot showing now is subscribed to, for the panel
        public string JointStateTopic => _jointStateSub != null ? _jointStateSub.Topic : string.Empty;

        private bool IsValid(int index) =>
            _robots != null && index >= 0 && index < _robots.Length && _robots[index].Prefab != null;

        private void Awake()
        {
            if (!IsValid(_selected))
            {
                Debug.LogWarning($"[XRViz] RobotSelector on '{name}' has no robot at index {_selected}.", this);
                return;
            }

            if (_current == null)
                _current = Instantiate(_robots[_selected].Prefab, transform.position, transform.rotation);

            // Before any Start, so the subscriber's first subscription is already the namespaced one
            ApplyNamespace(_current, NamespaceOf(_selected));
            Wire(_current, _robots[_selected], anchorToTf: false);
        }

        private void Start()
        {
            // In Start, not Awake: the handle captures its home pose in its own Awake, and
            // retargeting before that has run would compute the new home from an unset one
            if (_current != null && IsValid(_selected))
                RetargetHandle(_current, _robots[_selected]);

            Changed?.Invoke();
        }

        public bool Next(out string message) => Select(Wrap(_selected + 1), out message);

        public bool Previous(out string message) => Select(Wrap(_selected - 1), out message);

        private int Wrap(int index) => Count == 0 ? 0 : ((index % Count) + Count) % Count;

        public bool Select(int index, out string message)
        {
            if (!IsValid(index))
            {
                message = "That robot has no prefab assigned.";
                return false;
            }

            if (index == _selected && _current != null)
            {
                message = $"{LabelOf(index)} is already showing.";
                return false;
            }

            // A proposal on screen holds an undo pose for the robot about to be destroyed, and a
            // search in flight would apply to whichever robot is there when it finishes. Put the
            // old robot back first, so the pose carried over is the one the user had.
            if (_calibrator != null && (_calibrator.IsBusy || _calibrator.AwaitingConfirmation))
                _calibrator.CancelCalibration();

            // Carry over where the robot stands and how it is placed, so a switch changes WHICH
            // robot is there and nothing else
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            bool anchorToTf = false;
            bool visible = true;
            if (_current != null)
            {
                var oldRoot = FindRoot(_current);
                Transform rootTransform = oldRoot != null ? oldRoot.transform : _current.transform;
                position = rootTransform.position;
                rotation = rootTransform.rotation;
                anchorToTf = _anchor != null && _anchor.AnchorToTf;
                visible = _current.activeSelf;

                // Deactivated as well as destroyed: Destroy waits for the end of the frame, and
                // until then the old robot would still be found by anything sweeping the scene
                _current.SetActive(false);
                Destroy(_current);
            }

            var option = _robots[index];
            _selected = index;
            _current = Instantiate(option.Prefab, position, rotation);
            _current.name = "Robot";

            var root = FindRoot(_current);
            if (root != null)
                root.TeleportRoot(position, rotation);

            // Instantiate has run Awake but not Start, so this is still configuration
            ApplyNamespace(_current, NamespaceOf(index));

            // Handle before anchor: an anchor switched on places its target at once, through the
            // handle, and until retargeted that target is the old robot still waiting on Destroy
            RetargetHandle(_current, option);
            Wire(_current, option, anchorToTf);

            if (!visible)
                _current.SetActive(false);

            message = $"Robot: {option.Label}, on {JointStateTopic}.";
            Debug.Log($"[XRViz] {message}", this);
            Changed?.Invoke();
            return true;
        }

        // The components that make the robot behave like every other visualisation - a TF anchor
        // and a Scene row. Added here rather than by the scene generator because a robot spawned
        // at runtime needs them too, and two places defining them would drift.
        private void Wire(GameObject robot, RobotOption option, bool anchorToTf)
        {
            var visibility = robot.GetComponent<VisibilityTarget>();
            if (visibility == null)
                visibility = robot.AddComponent<VisibilityTarget>();
            visibility.SetTarget(robot);
            visibility.SetLabel("Robot");

            _anchor = robot.GetComponent<TfAnchor>();
            if (_anchor == null)
                _anchor = robot.AddComponent<TfAnchor>();
            _anchor.SetLabel("Robot");
            _anchor.SetFrameId(option.RootFrameId);
            _anchor.SetHandle(_handle);
            _anchor.SetAnchorToTf(anchorToTf);

            _jointStateSub = robot.GetComponentInChildren<RosSubscriberJointState>(true);

            if (_calibrator != null)
                _calibrator.SetRobotAnchor(_anchor);
            if (_status != null)
                _status.SetJointStateSubscriber(_jointStateSub);
        }

        private void RetargetHandle(GameObject robot, RobotOption option)
        {
            var root = FindRoot(robot);
            if (_handle == null || root == null)
                return;

            Vector3 handlePosition = option.UseHandlePosition
                ? option.HandlePosition
                : CornerOfBounds(robot, root.transform);

            // The handle's offset is the target relative to the handle - the reverse direction
            _handle.ReplaceTarget(null, root, -handlePosition);
        }

        // Prefixes every topic under the robot with its namespace. Leaves a topic already in the
        // namespace alone, so running over the generated scene's instance twice is harmless.
        private static void ApplyNamespace(GameObject robot, string ns)
        {
            if (string.IsNullOrEmpty(ns))
                return;

            string prefix = "/" + ns + "/";
            foreach (var binding in robot.GetComponentsInChildren<IRosTopicBinding>(true))
            {
                string topic = binding.Topic;
                if (string.IsNullOrEmpty(topic) || topic.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                binding.SetTopic(prefix + topic.TrimStart('/'));
            }
        }

        // The root of the ArticulationBody chain, which is a child link rather than the prefab's
        // own root for every robot here (trolley, platform, base_link, pelvis) - and the only
        // thing that can move the robot, since an ArticulationBody root ignores Transform writes.
        private static ArticulationBody FindRoot(GameObject robot)
        {
            var bodies = robot.GetComponentsInChildren<ArticulationBody>(true);
            return bodies.FirstOrDefault(b => b.isRoot) ?? bodies.FirstOrDefault();
        }

        // Top-front-left corner of what the robot looks like, in the root link's yaw frame -
        // the same corner the UR3e's hand-placed handle uses on its trolley
        private static Vector3 CornerOfBounds(GameObject robot, Transform root)
        {
            Quaternion toLocal = Quaternion.Inverse(Quaternion.Euler(0f, root.eulerAngles.y, 0f));
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            bool any = false;

            foreach (var renderer in robot.GetComponentsInChildren<Renderer>(true))
            {
                if (IsCollisionGeometry(renderer.transform, robot.transform))
                    continue;

                Bounds b = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 local = toLocal * (corner - root.position);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                }
                any = true;
            }

            if (!any)
                return new Vector3(0f, 0.5f, 0f);

            return new Vector3(min.x - k_HandleClearance, max.y + k_HandleClearance, min.z - k_HandleClearance);
        }

        private static bool IsCollisionGeometry(Transform t, Transform stopAt)
        {
            for (; t != null && t != stopAt; t = t.parent)
            {
                if (t.name == k_CollisionsObjectName)
                    return true;
            }
            return false;
        }
    }
}
