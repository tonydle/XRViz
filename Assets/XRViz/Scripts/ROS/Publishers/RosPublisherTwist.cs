using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosTwist = RosMessageTypes.Geometry.TwistMsg;

namespace Unity.Robotics
{
    public class RosPublisherTwist : RosPublisher<RosTwist>
    {
        [SerializeField] private Rigidbody _object;
        private Vector3 _previousPosition = Vector3.zero;
        private Quaternion _previousRotation = Quaternion.identity;
        protected override void Start()
        {
            base.Start();
            _message = new RosTwist();
        }
        
        public void Publish()
        {
            if(_object != null)
            {
                Publish(_object.linearVelocity, _object.angularVelocity);
            }
        }

        public void Publish(Vector3 linear, Vector3 angular)
        {
            _message.linear = linear.To<FLU>();
            _message.angular = angular.To<FLU>();
            Publish(_message);
        }
    }
}