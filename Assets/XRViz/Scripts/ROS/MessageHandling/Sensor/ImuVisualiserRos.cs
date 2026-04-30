using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Robotics
{
    public class ImuVisualiserRos : MonoBehaviour
    {
        [SerializeReference] private ArticulationBody robotBaseLink;
        [SerializeReference] private RosSubscriberImu imuSub;

        private GameObject _imuObject;
        private bool _childObjectFound = false;

        private void Update()
        {
            if(imuSub.isReady())
            {
                if(!_childObjectFound)
                {
                    _imuObject = GameObject.Find(imuSub.getFrameId()).gameObject;
                    if(_imuObject != null)
                    {
                        _childObjectFound = true;
                    }
                }
                if(_childObjectFound)
                {
                    Quaternion robotRot = imuSub.getLatestOrientation()*Quaternion.Inverse(_imuObject.transform.rotation);
                    robotRot = robotRot*robotBaseLink.transform.rotation;
                    robotBaseLink.TeleportRoot(robotBaseLink.transform.position, robotRot);
                }
            }
        }
    }
}