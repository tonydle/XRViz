using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosJoy = RosMessageTypes.Sensor.JoyMsg;

namespace Unity.Robotics
{
    public class RosSubscriberJoy : RosSubscriber<RosJoy>
    {
        private float[] _latestAxes;
        private int[] _latestButtons;
        private bool _messageAvailable = false;
        private RosJoy _joyMsg;

        protected override void Start()
        {
            base.Start();
        }

        protected override void Update()
        {
            base.Update();
            if (NewMessageAvailable())
            {
                _joyMsg = GetLatestMessage();
                _latestAxes = _joyMsg.axes;
                _latestButtons = _joyMsg.buttons;
                _messageAvailable = true;
            }
        }

        public bool IsAvailable()
        {
            return _messageAvailable;
        }

        public float[] GetLatestAxes()
        {
            if(_messageAvailable) _messageAvailable = false;
            return _latestAxes;
        }

        public int[] GetLatestButtons()
        {
            if(_messageAvailable) _messageAvailable = false;
            return _latestButtons;
        }
    }
}
