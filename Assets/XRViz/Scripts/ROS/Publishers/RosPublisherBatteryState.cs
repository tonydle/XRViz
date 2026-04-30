using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosBatteryState = RosMessageTypes.Sensor.BatteryStateMsg;

namespace Unity.Robotics
{
    public class RosPublisherBatteryState : RosPublisher<RosBatteryState>
    {
        [SerializeField] private string _frameID;
        [SerializeField] private float _percentage;
        protected override void Start()
        {
            base.Start();
            _message = new RosBatteryState();
        }

        public void Publish()
        {
            _message.header.frame_id = _frameID;
            _message.voltage = 5.0f;
            _message.percentage = _percentage;
            Publish(_message);
        }

        public void Publish(float percentage)
        {
            _percentage = percentage;
            Publish();
        }

        public void Publish(string frameID, float percentage)
        {
            _frameID = frameID;
            _percentage = percentage;
            Publish();
        }
    }
}