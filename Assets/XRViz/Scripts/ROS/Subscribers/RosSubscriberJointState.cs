using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosJointState = RosMessageTypes.Sensor.JointStateMsg;
using RosMessageTypes.BuiltinInterfaces;

namespace Unity.Robotics
{
    public class JointState
    {
        public List<string> name;
        public List<float> position;
        public List<float> velocity;
        public List<float> effort;

        public JointState()
        {
            name = new List<string>();
            position = new List<float>();
            velocity = new List<float>();
            effort = new List<float>();
        }

        public void Clear()
        {
            name.Clear();
            position.Clear();
            velocity.Clear();
            effort.Clear();
        }
    }

    public class RosSubscriberJointState : RosSubscriber<RosJointState>
    {
        private JointState _jointState = new JointState();
        private bool _ready = false;
        private RosJointState _jointStateMsg;
        private TimeMsg _latestTime;

        protected override void Start()
        {
            base.Start();

            _jointState = new JointState();
            _latestTime = new TimeMsg();
        }

        protected override void Update()
        {
            base.Update();
            if(NewMessageAvailable())
            {
                _ready = false;
                _jointState.Clear();
                _jointStateMsg = GetLatestMessage();
                
                // Update the latest time from the message header
                _latestTime = _jointStateMsg.header.stamp;
                
                foreach(string jointName in _jointStateMsg.name)
                {
                    _jointState.name.Add(jointName);
                }
                foreach(float jointPosition in _jointStateMsg.position)
                {
                    _jointState.position.Add(jointPosition);
                }
                foreach(float jointVelocity in _jointStateMsg.velocity)
                {
                    _jointState.velocity.Add(jointVelocity);
                }
                foreach(float jointEffort in _jointStateMsg.effort)
                {
                    _jointState.effort.Add(jointEffort);
                }
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public JointState getLatestJointState()
        {
            return _jointState;
        }

        public TimeMsg GetLatestTime()
        {
            return _latestTime;
        }
    }
}