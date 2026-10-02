using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Robotics
{
    [RequireComponent(typeof(ArticulationBody))]
    public class JointStateWriter : MonoBehaviour
    {
        private ArticulationBody _joint;
        private string _jointName;
        private bool _nameResolved;
        private float _position;

        private void Start()
        {
            _joint = this.GetComponent<ArticulationBody>();

            if(_joint.jointType == ArticulationJointType.RevoluteJoint) _joint.twistLock = ArticulationDofLock.LockedMotion;
            if(_joint.jointType == ArticulationJointType.PrismaticJoint) _joint.linearLockX = ArticulationDofLock.LockedMotion;

            if (!_nameResolved) ResolveJointName();
        }

        private void Update()
        {
            _joint.jointPosition = new ArticulationReducedSpace(_position);
        }

        public string GetName()
        {
            // Resolve lazily too: this component is added at runtime by
            // RobotStateWriterController, so a ROS message can land before Start has run
            if (!_nameResolved) ResolveJointName();
            return _jointName;
        }

        // The joint name comes from a baked UrdfJointName component rather than the importer's
        // UrdfJoint, because Unity.Robotics.UrdfImporter is not part of an Android build - see
        // the note on UrdfJointName.
        private void ResolveJointName()
        {
            _nameResolved = true;

            var baked = this.GetComponent<UrdfJointName>();
            if (baked != null && !string.IsNullOrEmpty(baked.JointName))
            {
                _jointName = baked.JointName;
                return;
            }

#if UNITY_EDITOR || UNITY_STANDALONE
            // Desktop and Play Mode still have the importer, so fall back to reading the joint
            // name straight off it. Warn loudly: this path does not exist in a Quest APK, and
            // without a baked name the robot will silently stop tracking /joint_states there.
            UrdfImporter.UrdfJoint urdfJoint = this.GetComponent<UrdfImporter.UrdfJointRevolute>();
            if(urdfJoint == null) urdfJoint = this.GetComponent<UrdfImporter.UrdfJointContinuous>();
            if(urdfJoint == null) urdfJoint = this.GetComponent<UrdfImporter.UrdfJointPrismatic>();
            if(urdfJoint != null)
            {
                _jointName = urdfJoint.jointName;
                Debug.LogWarning($"[XRViz] '{name}' has no baked UrdfJointName, falling back to the " +
                    "URDF Importer. Run XRViz > Bake URDF Joint Names on the robot before building " +
                    "for Android, or its joints will not move in the APK.", this);
                return;
            }
#endif

            Debug.LogWarning($"[XRViz] Could not resolve a URDF joint name for '{name}'; it will not " +
                "match anything in /joint_states. Run XRViz > Bake URDF Joint Names on the robot.", this);
        }

        public void Write(float position)
        {
            _position = position;
        }
    }
}
