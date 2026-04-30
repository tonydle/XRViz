using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosInt8 = RosMessageTypes.Std.Int8Msg;

namespace Unity.Robotics
{
    public class RosSubscriberInt8 : RosSubscriber<RosInt8>
    {
        private bool _ready = false;
        private RosInt8 _int8Msg;

        private int _value;

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
                _int8Msg = GetLatestMessage();
                _value = _int8Msg.data;
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public int getLatestValue()
        {
            return _value;
        }
    }
}