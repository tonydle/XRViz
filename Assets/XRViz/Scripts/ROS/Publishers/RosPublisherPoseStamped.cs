using System;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosPoseStamped = RosMessageTypes.Geometry.PoseStampedMsg;

namespace Unity.Robotics
{
    public class RosPublisherPoseStamped : RosPublisher<RosPoseStamped>
    {
        [Tooltip("Leave empty to set as World frame")]
        [SerializeField] private Transform _parentFrame;
        [SerializeField] private Transform _object;
        
        // Auto publishing
        [SerializeField] private bool _autoPublish = false;

        protected override void Start()
        {
            base.Start();
            _message = new RosPoseStamped();
        }

        private void Update()
        {
            if(_autoPublish) Publish();
        }
        
        public void Publish()
        {
            if(_object != null)
            {
                if(_parentFrame != null) Publish(_parentFrame, _object);
                else Publish(_object);
            }
        }

        public void Publish(Transform obj)
        {
            _message.header.frame_id = "world";

            DateTime now = DateTime.UtcNow;
            long epochMilliseconds = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            _message.header.stamp.sec = (int)(epochMilliseconds / 1000);
            _message.header.stamp.nanosec = (uint)((epochMilliseconds % 1000) * 1000000);

            _message.pose.position = obj.position.To<FLU>();
            _message.pose.orientation = obj.rotation.To<FLU>();
            Publish(_message);
        }

        public void Publish(Transform frame, Transform obj)
        {
            _message.header.frame_id = frame.name;
            
            DateTime now = DateTime.UtcNow;
            long epochMilliseconds = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            _message.header.stamp.sec = (int)(epochMilliseconds / 1000);
            _message.header.stamp.nanosec = (uint)((epochMilliseconds % 1000) * 1000000);

            _message.pose.position = frame.InverseTransformPoint(obj.position).To<FLU>();
            _message.pose.orientation = (Quaternion.Inverse(frame.rotation) * obj.rotation).To<FLU>();
            Publish(_message);
        }
    }
}