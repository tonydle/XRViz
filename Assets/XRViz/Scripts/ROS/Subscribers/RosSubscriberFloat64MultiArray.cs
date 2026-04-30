using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosFloat64MultiArray = RosMessageTypes.Std.Float64MultiArrayMsg;

namespace Unity.Robotics
{
    public class RosSubscriberFloat64MultiArray : RosSubscriber<RosFloat64MultiArray>
    {
        private bool _ready = false;
        private RosFloat64MultiArray _float64MultiArrayMsg;

        private double[] _data;

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
                _float64MultiArrayMsg = GetLatestMessage();
                _data = _float64MultiArrayMsg.data;
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public double[] getLatestData()
        {
            return _data;
        }
    }
}