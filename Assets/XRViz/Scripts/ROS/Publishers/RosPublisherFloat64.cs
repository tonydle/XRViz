using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosFloat64 = RosMessageTypes.Std.Float64Msg;

namespace Unity.Robotics
{
    public class RosPublisherFloat64 : RosPublisher<RosFloat64>
    {
        [SerializeField] private double _data;
        protected override void Start()
        {
            base.Start();
            _message = new RosFloat64();
        }

        public void Publish()
        {
            _message.data = _data;
            Publish(_message);
        }

        public void Publish(double data)
        {
            _message.data = data;
            Publish(_message);
        }
    }
}