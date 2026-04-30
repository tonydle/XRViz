using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using System.Collections.Generic;
using RosImu = RosMessageTypes.Sensor.ImuMsg;

namespace Unity.Robotics
{
    public class RosSubscriberImu : RosSubscriber<RosImu>
    {
        private Quaternion _orientation = new Quaternion();
        private Vector3 _angular_vel = new Vector3();
        private Vector3 _linear_acc = new Vector3();
        private string _frame_id = "";
        private bool _ready = false;
        private RosImu _imu;

        protected override void Start()
        {
            base.Start();
        }

        protected override void Update()
        {
            base.Update();
            if(NewMessageAvailable())
            {
                _ready = false;
                _imu = GetLatestMessage();
                _orientation = _imu.orientation.From<FLU>();
                _angular_vel = _imu.angular_velocity.From<FLU>();
                _linear_acc = _imu.linear_acceleration.From<FLU>();
                _frame_id = _imu.header.frame_id;
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public string getFrameId()
        {
            return _frame_id;
        }

        public Quaternion getLatestOrientation()
        {
            return _orientation;
        }

        public Vector3 getLatestAngularVelocity()
        {
            return _angular_vel;
        }

        public Vector3 getLatestLinearAcceleration()
        {
            return _linear_acc;
        }
    }
}