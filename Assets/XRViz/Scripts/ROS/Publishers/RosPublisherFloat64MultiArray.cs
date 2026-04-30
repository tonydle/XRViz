using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosFloat64MultiArray = RosMessageTypes.Std.Float64MultiArrayMsg;

namespace Unity.Robotics
{
    public class RosPublisherFloat64MultiArray : RosPublisher<RosFloat64MultiArray>
    {
        [SerializeField] private double[] _data;
        protected override void Start()
        {
            base.Start();
            _message = new RosFloat64MultiArray();
        }

        public void Publish()
        {
            _message.data = _data;
            Publish(_message);
        }

        public void Publish(double[] data)
        {
            _message.data = data;
            Publish(_message);
        }
    }
}