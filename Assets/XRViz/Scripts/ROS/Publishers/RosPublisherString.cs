using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosString = RosMessageTypes.Std.StringMsg;

namespace Unity.Robotics
{
    public class RosPublisherString : RosPublisher<RosString>
    {
        [SerializeField] private string _data = "";
        protected override void Start()
        {
            base.Start();
            _message = new RosString(_data);
        }

        public void Publish()
        {
            _message.data = _data;
            Publish(_message);
        }

        public void Publish(string data)
        {
            _message.data = data;
            Publish(_message);
        }
    }
}