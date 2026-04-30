using System;
using UnityEngine;
using RosJoy = RosMessageTypes.Sensor.JoyMsg;

namespace Unity.Robotics
{
    public class RosPublisherJoy : RosPublisher<RosJoy>
    {
        [SerializeField] private string _frameID;
        [SerializeField] private float[] _axes;
        [SerializeField] private int[] _buttons;
        protected override void Start()
        {
            base.Start();
            _message = new RosJoy();
        }

        public void Publish()
        {
            _message.header.frame_id = _frameID;

            DateTime now = DateTime.UtcNow;
            long epochMilliseconds = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            _message.header.stamp.sec = (int)(epochMilliseconds / 1000);
            _message.header.stamp.nanosec = (uint)((epochMilliseconds % 1000) * 1000000);

            _message.axes = _axes;
            _message.buttons = _buttons;
            Publish(_message);
        }

        public void Publish(float[] axes, int[] buttons)
        {
            _axes = axes;
            _buttons = buttons;
            Publish();
        }

        public void Publish(string frameID, float[] axes, int[] buttons)
        {
            _frameID = frameID;
            _axes = axes;
            _buttons = buttons;
            Publish();
        }
    }
}