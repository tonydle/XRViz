using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosFloat32 = RosMessageTypes.Std.Float32Msg;

namespace Unity.Robotics
{
    public class RosSubscriberFloat32 : RosSubscriber<RosFloat32>
    {
        private bool _messageAvailable = false;
        private RosFloat32 _float32Msg;

        private float _value;

        protected override void Start()
        {
            base.Start();
        }

        protected override void Update()
        {
            base.Update();
            if(NewMessageAvailable())
            {
                _float32Msg = GetLatestMessage();
                _value = _float32Msg.data;
                _messageAvailable = true;
            }
        }

        public bool IsAvailable()
        {
            return _messageAvailable;
        }

        public float getLatestValue()
        {
            return _value;
        }
    }
}