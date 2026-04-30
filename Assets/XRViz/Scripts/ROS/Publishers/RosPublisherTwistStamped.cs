using System;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosTwistStamped = RosMessageTypes.Geometry.TwistStampedMsg;
using RosTwist = RosMessageTypes.Geometry.TwistMsg;

namespace Unity.Robotics
{
    public class RosPublisherTwistStamped : RosPublisher<RosTwistStamped>
    {
        [SerializeField] private Rigidbody _object;
        public string frameId = "base_link";
        private Vector3 _previousPosition = Vector3.zero;
        private Quaternion _previousRotation = Quaternion.identity;

        protected override void Start()
        {
            base.Start();
            _message = new RosTwistStamped
            {
                header = new RosMessageTypes.Std.HeaderMsg(),
                twist = new RosTwist()
            };
        }

        public void Publish()
        {
            if (_object != null)
            {
                Publish(_object.linearVelocity, _object.angularVelocity);
            }
        }

        public void Publish(Vector3 linearVelocity, Vector3 angularVelocity)
        {
            // Calculate seconds and nanoseconds from Unix epoch time
            DateTime now = DateTime.UtcNow;
            long epochMilliseconds = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            long epochSeconds = epochMilliseconds / 1000;
            long epochNanoseconds = (epochMilliseconds % 1000) * 1000000;

            Publish(linearVelocity, angularVelocity, (int)epochSeconds, (uint)epochNanoseconds);
        }

        public void Publish(Vector3 linearVelocity, Vector3 angularVelocity, int seconds, uint nanoseconds)
        {
            // Set the message header timestamp
            _message.header.stamp.sec = (int)seconds;
            _message.header.stamp.nanosec = (uint)nanoseconds;

            _message.header.frame_id = frameId;
            _message.twist.linear = linearVelocity.To<FLU>();
            _message.twist.angular = angularVelocity.To<FLU>();
            Publish(_message);
        }
    }
}