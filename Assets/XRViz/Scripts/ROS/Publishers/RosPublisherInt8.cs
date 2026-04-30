using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosInt8 = RosMessageTypes.Std.Int8Msg;

namespace Unity.Robotics
{
    public class RosPublisherInt8 : RosPublisher<RosInt8>
    {
        [SerializeField] private sbyte _data = 0;
        protected override void Start()
        {
            base.Start();
            _message = new RosInt8(_data);
        }

        public void Publish()
        {
            _message.data = _data;
            Publish(_message);
        }

        public void Publish(int data)
        {
            _message.data = (sbyte)data;
            Publish(_message);
        }
    }
}