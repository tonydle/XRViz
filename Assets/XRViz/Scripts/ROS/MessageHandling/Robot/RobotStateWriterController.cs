using System;
using Unity.Robotics;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Robotics
{
    public class RobotStateWriterController : MonoBehaviour
    {
        private List<ArticulationBody> _articulationChain;
        private List<float> _jointPositions;
        private List<JointStateWriter> _jointStateWriters;

        [System.Serializable]
        public class MimicJoint
        {
            public string targetJointName; // the joint this mimic joint mimics
            public string mimicJointName; // the name of the mimic joint
            public float multiplier = 1f;
            public float offset = 0f;
        }
        
        [SerializeField]
        private List<MimicJoint> mimicJoints = new List<MimicJoint>();

        void Start()
        {
            _jointPositions = new List<float>();
            _jointStateWriters = new List<JointStateWriter>();
            _articulationChain = new List<ArticulationBody>(this.GetComponentsInChildren<ArticulationBody>());
            foreach (ArticulationBody joint in _articulationChain)
            {
                joint.useGravity = false;
                UrdfImporter.UrdfCollisions urdfCollision = joint.gameObject.GetComponentInChildren<UrdfImporter.UrdfCollisions>();
                if(urdfCollision != null) 
                    urdfCollision.gameObject.SetActive(false);
                if(joint.jointType != ArticulationJointType.FixedJoint)
                {
                    joint.gameObject.AddComponent<JointStateWriter>();
                    _jointStateWriters.Add(joint.gameObject.GetComponent<JointStateWriter>());
                    _jointPositions.Add(joint.jointPosition[0]);
                }
            }
        }

        void Update()
        {
            for(int index = 0; index < _jointStateWriters.Count; index++)
            {
                _jointStateWriters[index].Write(_jointPositions[index]);
            }
        }

        public void SetState(List<string> jointNames, List<float> jointPositions)
        {
            // First update direct joint states from the provided jointNames.
            for (int index = 0; index < jointNames.Count; index++)
            {
                int localIndex = _jointStateWriters.FindIndex(jsw => jsw.GetName() == jointNames[index]);
                if (localIndex != -1 && !float.IsNaN(jointPositions[index]))
                {
                    _jointPositions[localIndex] = jointPositions[index];
                }
            }

            // Now propagate mimic joint updates until the values stabilize.
            bool updated;
            int maxIterations = 10;  // Prevent potential infinite loops in case of cycles.
            int iteration = 0;
            do
            {
                updated = false;
                foreach (var mimicJoint in mimicJoints)
                {
                    // Get the current value of the mimic joint from our writer list (not from jointNames).
                    int mimicSourceIndex = _jointStateWriters.FindIndex(jsw => jsw.GetName() == mimicJoint.mimicJointName);
                    int targetIndex = _jointStateWriters.FindIndex(jsw => jsw.GetName() == mimicJoint.targetJointName);

                    if (mimicSourceIndex != -1 && targetIndex != -1)
                    {
                        // Calculate the desired value for the target joint.
                        float desiredValue = _jointPositions[mimicSourceIndex] * mimicJoint.multiplier + mimicJoint.offset;
                        // If the target joint value differs (using Approximately for float comparisons), update it.
                        if (!Mathf.Approximately(_jointPositions[targetIndex], desiredValue))
                        {
                            _jointPositions[targetIndex] = desiredValue;
                            updated = true;
                        }
                    }
                }
                iteration++;
            } while (updated && iteration < maxIterations);
        }
    }
}
