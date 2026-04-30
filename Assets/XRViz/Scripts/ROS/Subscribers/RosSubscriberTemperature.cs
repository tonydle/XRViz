using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Sensor;
using RosTemperature = RosMessageTypes.Sensor.TemperatureMsg;

namespace Unity.Robotics
{
    public class RosSubscriberTemperature : RosSubscriber<RosTemperature>
    {
        private bool _ready = false;
        private RosTemperature _temperatureMsg;
        private float _temperature;
        private string _frameId;

        protected override void Start()
        {
            base.Start();
        }

        protected override void Update()
        {
            base.Update();
            if (NewMessageAvailable())
            {
                _ready = false;
                _temperatureMsg = GetLatestMessage();
                _temperature = (float)_temperatureMsg.temperature;
                _frameId = _temperatureMsg.header.frame_id;
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public float getLatestTemperature()
        {
            return _temperature;
        }

        public string getFrameId()
        {
            return _frameId;
        }
    }
}
