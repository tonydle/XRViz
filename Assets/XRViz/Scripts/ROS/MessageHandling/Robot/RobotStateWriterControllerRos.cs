using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Robotics
{
    public class RobotStateWriterControllerRos : MonoBehaviour
    {
        [SerializeReference] private RobotStateWriterController stateWriterController;
        [SerializeReference] private RosSubscriberJointState jointStateSub;

        private void Update()
        {
            if(jointStateSub.isReady())
            {
                JointState latestState = jointStateSub.getLatestJointState();
                stateWriterController.SetState(latestState.name, latestState.position);
            }
        }
    }
}