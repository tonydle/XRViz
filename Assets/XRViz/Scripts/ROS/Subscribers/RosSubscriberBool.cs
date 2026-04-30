using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosBool = RosMessageTypes.Std.BoolMsg;

namespace Unity.Robotics
{
    public class RosSubscriberBool : RosSubscriber<RosBool>
    {
        private bool _ready = false;
        private RosBool _boolMsg;

        private bool _value;

        protected override void Start()
        {
            base.Start();
        }

        protected override void Update()
        {
            base.Update();
            if(NewMessageAvailable())
            {
                _ready = false;
                _boolMsg = GetLatestMessage();
                _value = _boolMsg.data;
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public bool getLatestValue()
        {
            return _value;
        }
    }
}