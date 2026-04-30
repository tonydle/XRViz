using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Robotics
{
    [RequireComponent(typeof(ArticulationBody))]
    public class JointStateWriter : MonoBehaviour
    {
        private ArticulationBody _joint;
        private UrdfImporter.UrdfJoint _urdfJoint;
        private string _jointName;
        private float _position;
        private void Start()
        {
            _joint = this.GetComponent<ArticulationBody>();

            if(_joint.jointType == ArticulationJointType.RevoluteJoint) _joint.twistLock = ArticulationDofLock.LockedMotion;
            if(_joint.jointType == ArticulationJointType.PrismaticJoint) _joint.linearLockX = ArticulationDofLock.LockedMotion;

            _urdfJoint = this.GetComponent<UrdfImporter.UrdfJointRevolute>();
            if(_urdfJoint == null) _urdfJoint = this.GetComponent<UrdfImporter.UrdfJointContinuous>();
            if(_urdfJoint == null) _urdfJoint = this.GetComponent<UrdfImporter.UrdfJointPrismatic>();
            if(_urdfJoint != null) _jointName = _urdfJoint.jointName;
        }

        private void Update()
        {
            _joint.jointPosition = new ArticulationReducedSpace(_position);
        }

        public string GetName()
        {
            return _jointName;
        }

        public void Write(float position)
        {
            _position = position;
        }
    }
}