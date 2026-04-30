using UnityEngine;
using RosMessageTypes.BuiltinInterfaces;

namespace Unity.Robotics
{
    public class XRDirectTwistPublisher : MonoBehaviour
    {
        [Header("VR Transforms")]
        [Tooltip("VR user head transform (origin of the user’s pose)")]
        [SerializeField] private Transform userHeadTransform;
        [Tooltip("VR user hand transform (from a controller or future hand tracking)")]
        [SerializeField] private Transform userHandTransform;

        [Header("Robot Reference")]
        [Tooltip("Robot head transform acting as the reference frame (if the user were the robot)")]
        [SerializeField] private Transform robotHeadTransform;

        [Header("End Effector Transform")]
        [Tooltip("The robot end effector transform")]
        [SerializeField] private Transform endEffectorTransform;

        [SerializeField, Tooltip("Used as the reference frame for servoing calculations")]
        private Transform _referenceFrame;

        [Header("ROS Publisher Settings")]
        [Tooltip("ROS twist command topic name")]
        [SerializeField] private string topicName = "/servo_server/delta_twist_cmds";

        [Header("PID Gains")]
        [Tooltip("PID gains for position (x->P, y->I, z->D)")]
        [SerializeField] private Vector3 pidGainsPosition = new Vector3(1.0f, 0.0f, 0.0f);
        [Tooltip("PID gains for rotation (x->P, y->I, z->D)")]
        [SerializeField] private Vector3 pidGainsRotation = new Vector3(1.0f, 0.0f, 0.0f);

        [Header("Axis Constraints")]
        [Tooltip("Scaling on each axis of position motion")]
        [SerializeField] private Vector3 positionAxisScale = new Vector3(1, 1, 1);
        [Tooltip("Scaling on each axis of rotation motion")]
        [SerializeField] private Vector3 rotationAxisScale = new Vector3(1, 1, 1);

        [Header("Position Constraints (Optional)")]
        [Tooltip("Toggle constraints for each axis in robot-local coordinates")]
        [SerializeField] private Vector3Bool usePositionConstraints;
        [Tooltip("Minimum position constraints in robot local coordinates")]
        [SerializeField] private Vector3 minPositionConstraints = new Vector3(-1, -1, -1);
        [Tooltip("Maximum position constraints in robot local coordinates")]
        [SerializeField] private Vector3 maxPositionConstraints = new Vector3(1, 1, 1);

        [System.Serializable]
        public struct Vector3Bool
        {
            public bool x;
            public bool y;
            public bool z;

            public Vector3Bool(bool x, bool y, bool z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }
        }

        private PIDController positionController;
        private PIDController rotationController;

        private RosPublisherTwistStamped twistStampedPublisher;
        private RosSubscriberJointState jointStateSubscriber;

        // Debug block for visualizing the computed target pose.
        private GameObject debugBlock;

        private void Awake()
        {
            jointStateSubscriber = GetComponent<RosSubscriberJointState>();

            twistStampedPublisher = gameObject.AddComponent<RosPublisherTwistStamped>();
            twistStampedPublisher.SetTopic(topicName);

            positionController = new PIDController(pidGainsPosition);
            rotationController = new PIDController(pidGainsRotation);

            // Create a small debug block to visualize the target pose.
            debugBlock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            debugBlock.name = "DebugTargetBlock";
            debugBlock.transform.localScale = new Vector3(0.02f, 0.02f, 0.02f);
            // Optionally disable the collider so it doesn't interfere.
            Destroy(debugBlock.GetComponent<Collider>());
        }

        private void Update()
        {
            if (userHeadTransform == null || userHandTransform == null || robotHeadTransform == null)
            {
                Debug.LogError("One or more required transforms are missing.");
                return;
            }
            if (jointStateSubscriber == null || !jointStateSubscriber.isReady())
                return;

            CalculateAndPublishTwist();
        }

        private void CalculateAndPublishTwist()
        {
            // Get the latest ROS time stamp.
            TimeMsg latestTime = jointStateSubscriber.GetLatestTime();

            // Calculate the relative rotation and position from A to B.
            Quaternion relativeRotation = Quaternion.Inverse(userHeadTransform.rotation) * userHandTransform.rotation;
            Vector3 relativePosition = Quaternion.Inverse(userHeadTransform.rotation) * (userHandTransform.position - userHeadTransform.position);

            // Update the debug block's position and rotation to visualize the target pose.
            if (debugBlock != null)
            {
                debugBlock.transform.SetPositionAndRotation(robotHeadTransform.position + robotHeadTransform.rotation * relativePosition, robotHeadTransform.rotation * relativeRotation);
            }

            // Compute position error: difference between debug block (target pose) and the current end effector position.
            Vector3 positionError = debugBlock.transform.position - endEffectorTransform.position;
            positionError = _referenceFrame.InverseTransformDirection(positionError);

            Quaternion rotationError = debugBlock.transform.rotation * Quaternion.Inverse(endEffectorTransform.rotation);
            Vector3 rotationErrorEuler = rotationError.eulerAngles;

            // Convert angles from 0-360 to -180 to 180.
            if (rotationErrorEuler.x > 180) rotationErrorEuler.x -= 360;
            if (rotationErrorEuler.y > 180) rotationErrorEuler.y -= 360;
            if (rotationErrorEuler.z > 180) rotationErrorEuler.z -= 360;

            rotationErrorEuler = _referenceFrame.InverseTransformDirection(rotationErrorEuler);
            rotationErrorEuler *= -1.0f;

            // Use PID controllers to compute twist commands.
            Vector3 twistLinear = positionController.Update(positionError, Vector3.zero, Time.deltaTime);
            Vector3 twistAngular = rotationController.Update(rotationErrorEuler, Vector3.zero, Time.deltaTime);

            twistLinear = Vector3.Scale(twistLinear, positionAxisScale);
            twistAngular = Vector3.Scale(twistAngular, rotationAxisScale);

            twistStampedPublisher.Publish(twistLinear, twistAngular, latestTime.sec, latestTime.nanosec);
        }
    }
}
