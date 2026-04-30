using UnityEngine;
using RosMessageTypes.BuiltinInterfaces;

namespace Unity.Robotics
{
    public class PIDController
    {
        public Vector3 Gains { get; set; } // x -> P, y -> I, z -> D

        private Vector3 previousError;
        private Vector3 integral;

        public PIDController(Vector3 gains)
        {
            Gains = gains;
        }

        public Vector3 Update(Vector3 setpoint, Vector3 actual, float deltaTime)
        {
            var error = setpoint - actual;
            integral += error * deltaTime;
            var derivative = (error - previousError) / deltaTime;
            previousError = error;

            return new Vector3(
                error.x * Gains.x + integral.x * Gains.y + derivative.x * Gains.z,
                error.y * Gains.x + integral.y * Gains.y + derivative.y * Gains.z,
                error.z * Gains.x + integral.z * Gains.y + derivative.z * Gains.z
            );
        }
    }

    public class XRRobotTwistPublisher : MonoBehaviour
    {
        [SerializeField, Tooltip("Reference to the XREndEffectorGrabbable script")]
        private XREndEffectorGrabbable endEffectorGrabbable;

        [SerializeField, Tooltip("Reference to the ROS subscriber for joint states")]
        private RosSubscriberJointState jointStateSubscriber;

        public enum ModeOfOperation
        {
            ActivatedMode,
            SelectedMode
        }
        [Header("Mode of Operation")]
        [SerializeField, Tooltip("Choose between Activated or Selected mode for operation")]
        private ModeOfOperation operationMode = ModeOfOperation.ActivatedMode;

        [SerializeField, Tooltip("ROS twist command topic name")]
        private string topicName = "/servo_server/delta_twist_cmds";

        [SerializeField, Tooltip("Used as the reference frame for calculations")]
        private Transform _referenceFrame;

        [Header("PID Gains")]
        [SerializeField, Tooltip("PID gains for position (x->P, y->I, z->D)")]
        private Vector3 pidGainsPosition = new Vector3(1.0f, 0.0f, 0.0f);

        [SerializeField, Tooltip("PID gains for rotation (x->P, y->I, z->D)")]
        private Vector3 pidGainsRotation = new Vector3(1.0f, 0.0f, 0.0f);

        [Header("Axis Constraints")]
        [SerializeField, Tooltip("Scaling on each axis of position motion")]
        private Vector3 positionAxisScale = new Vector3(1, 1, 1);

        [SerializeField, Tooltip("Scaling on each axis of rotation motion")]
        private Vector3 rotationAxisScale = new Vector3(1, 1, 1);

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
        [Header("Position Constraints Toggles")]
        [SerializeField, Tooltip("Toggle constraints for each axis.")]
        private Vector3Bool usePositionConstraints;
        [Header("Position Constraints")]
        [SerializeField, Tooltip("Minimum position constraints relative to the reference frame.")]
        private Vector3 minPositionConstraints = new Vector3(-1, -1, -1);

        [SerializeField, Tooltip("Maximum position constraints relative to the reference frame.")]
        private Vector3 maxPositionConstraints = new Vector3(1, 1, 1);

        private PIDController positionController;
        private PIDController rotationController;
        
        private RosPublisherTwistStamped twistStampedPublisher;
        private bool wasActive = false;

        private void Awake()
        {
            twistStampedPublisher = gameObject.AddComponent<RosPublisherTwistStamped>();
            twistStampedPublisher.SetTopic(topicName);
            twistStampedPublisher.frameId = _referenceFrame != null ? _referenceFrame.name : "base_link";

            positionController = new PIDController(pidGainsPosition);
            rotationController = new PIDController(pidGainsRotation);
        }

        private void Update()
        {
            if (endEffectorGrabbable == null) { Debug.LogError("XRendEffectorGrabbable reference is missing"); return; }
            if (_referenceFrame == null) { Debug.LogError("Reference frame is missing"); return; }

            bool active;
            switch (operationMode)
            {
                case ModeOfOperation.ActivatedMode:
                    active = endEffectorGrabbable.IsActivated();
                    break;
                case ModeOfOperation.SelectedMode:
                    active = endEffectorGrabbable.IsSelected();
                    break;
                default:
                    active = false;
                    break;
            }

            if (active)
            {
                CalculateTwistAndPublish();
            }
            else if (wasActive)
            {
                PublishZeroTwist();
            }

            wasActive = active;
        }

        private void CalculateTwistAndPublish()
        {
            if (twistStampedPublisher == null || jointStateSubscriber == null || !jointStateSubscriber.isReady()) return;
            
            // Get the latest time from the jointStateSubscriber
            TimeMsg latestTime = jointStateSubscriber.GetLatestTime();

            CalculateDifference(out Vector3 positionDifference, out Vector3 rotationDifference);

            Vector3 twistLinear = positionController.Update(positionDifference, Vector3.zero, Time.deltaTime);
            Vector3 twistAngular = rotationController.Update(rotationDifference, Vector3.zero, Time.deltaTime);

            twistLinear = Vector3.Scale(twistLinear, positionAxisScale); // Apply position axis constraints
            twistAngular = Vector3.Scale(twistAngular, rotationAxisScale); // Apply rotation axis constraints
            
            twistStampedPublisher.Publish(twistLinear, twistAngular, latestTime.sec, latestTime.nanosec);
        }

        private void CalculateDifference(out Vector3 positionDifference, out Vector3 rotationDifference)
        {
            endEffectorGrabbable.GetEndEffectorPositionAndRotation(out Vector3 endEffectorPosition, out Quaternion endEffectorRotation);
            endEffectorGrabbable.GetTargetPositionAndRotation(out Vector3 targetPosition, out Quaternion targetRotation);

            // Clamp targetPosition based on the defined constraints, after converting it to the reference frame's local coordinates
            Vector3 localTargetPosition = _referenceFrame.InverseTransformPoint(targetPosition);
            localTargetPosition = new Vector3(
                usePositionConstraints.x ? Mathf.Clamp(localTargetPosition.x, minPositionConstraints.x, maxPositionConstraints.x) : localTargetPosition.x,
                usePositionConstraints.y ? Mathf.Clamp(localTargetPosition.y, minPositionConstraints.y, maxPositionConstraints.y) : localTargetPosition.y,
                usePositionConstraints.z ? Mathf.Clamp(localTargetPosition.z, minPositionConstraints.z, maxPositionConstraints.z) : localTargetPosition.z
            );
            targetPosition = _referenceFrame.TransformPoint(localTargetPosition);
            
            positionDifference = targetPosition - endEffectorPosition;
            positionDifference = _referenceFrame.InverseTransformDirection(positionDifference);

            Quaternion rotationDifferenceQuaternion = targetRotation * Quaternion.Inverse(endEffectorRotation);
            rotationDifference = rotationDifferenceQuaternion.eulerAngles;

            // Convert angles from 0-360 to -180-180
            rotationDifference.x = (rotationDifference.x > 180) ? rotationDifference.x - 360 : rotationDifference.x;
            rotationDifference.y = (rotationDifference.y > 180) ? rotationDifference.y - 360 : rotationDifference.y;
            rotationDifference.z = (rotationDifference.z > 180) ? rotationDifference.z - 360 : rotationDifference.z;

            rotationDifference = _referenceFrame.InverseTransformDirection(rotationDifference);
            rotationDifference *= -1.0f; // not sure why, probably error in Quaternion calculation
        }

        private void PublishZeroTwist()
        {
            if (twistStampedPublisher == null || jointStateSubscriber == null || !jointStateSubscriber.isReady()) return;
            
            // Get the latest time from the jointStateSubscriber
            TimeMsg latestTime = jointStateSubscriber.GetLatestTime();

            twistStampedPublisher.Publish(Vector3.zero, Vector3.zero, latestTime.sec, latestTime.nanosec);
        }
    }
}