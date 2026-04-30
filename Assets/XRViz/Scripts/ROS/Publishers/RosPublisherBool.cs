using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosBool = RosMessageTypes.Std.BoolMsg;

namespace Unity.Robotics
{
    public class RosPublisherBool : RosPublisher<RosBool>
    {
        [SerializeField] private bool _data = false;
        protected override void Start()
        {
            base.Start();
            _message = new RosBool(_data);
        }

        public void Publish()
        {
            _message.data = _data;
            Publish(_message);
        }

        public void Publish(bool data)
        {
            _message.data = data;
            Publish(_message);
        }

        public void Toggle()
        {
            _data = !_data;
            Publish();
        }
    }
}