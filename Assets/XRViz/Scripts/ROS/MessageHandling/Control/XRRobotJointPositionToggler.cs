using UnityEngine;

namespace Unity.Robotics
{
    [RequireComponent(typeof(XREndEffectorGrabbable))]
    public class XRRobotJointPositionToggler : MonoBehaviour
    {
        [SerializeField, Tooltip("Reference to the XREndEffectorGrabbable script")]
        private XREndEffectorGrabbable endEffectorGrabbable;

        public enum ModeOfOperation
        {
            ActivatedMode,
            SelectedMode
        }
        [Header("Mode of Operation")]
        [SerializeField, Tooltip("Choose between Activated or Selected mode for operation")]
        private ModeOfOperation operationMode = ModeOfOperation.ActivatedMode;

        [SerializeField, Tooltip("ROS joint position topic name")]
        private string topicName = "/joint_position_controller/command";

        [Header("Joint Position Values")]
        [SerializeField, Tooltip("Value to publish when activated or selected")]
        private double valueWhenActive = 1.0;

        [SerializeField, Tooltip("Value to publish when not activated or not selected")]
        private double valueWhenNotActive = 0.0;

        [Header("Transition")]
        [SerializeField, Tooltip("Speed factor for the transition between active and non-active values")]
        private float transitionSpeed = 1.0f;

        private RosPublisherFloat64 jointPositionPublisher;
        private double currentTargetValue;
        private double currentValue;

        private void Awake()
        {
            jointPositionPublisher = gameObject.AddComponent<RosPublisherFloat64>();
            jointPositionPublisher.SetTopic(topicName);
            currentValue = valueWhenNotActive;  // assuming it starts as not active
            currentTargetValue = currentValue;
        }

        private void Update()
        {
            if (endEffectorGrabbable == null) { Debug.LogError("XRendEffectorGrabbable reference is missing"); return; }

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

            currentTargetValue = active ? valueWhenActive : valueWhenNotActive;

            // Smoothly transition currentValue towards currentTargetValue
            currentValue = Mathf.Lerp((float)currentValue, (float)currentTargetValue, Time.deltaTime * transitionSpeed);

            jointPositionPublisher.Publish(currentValue);
        }
    }
}