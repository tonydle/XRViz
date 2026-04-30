using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosEmpty = RosMessageTypes.Std.EmptyMsg;

namespace Unity.Robotics
{
    public class RosPublisherEmpty : RosPublisher<RosEmpty>
    {
        protected override void Start()
        {
            base.Start();
            _message = new RosEmpty();
        }

        public void Publish()
        {
            Publish(_message);
        }
    }
}