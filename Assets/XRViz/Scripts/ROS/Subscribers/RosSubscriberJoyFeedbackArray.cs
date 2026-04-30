using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosJoyFeedbackArray = RosMessageTypes.Sensor.JoyFeedbackArrayMsg;
using RosJoyFeedback = RosMessageTypes.Sensor.JoyFeedbackMsg;

namespace Unity.Robotics
{
    public class RosSubscriberJoyFeedbackArray : RosSubscriber<RosJoyFeedbackArray>
    {
        private bool _ready = false;
        private RosJoyFeedbackArray _joyFeedbackArrayMsg;

        private Color _rgb = new Color(1, 0, 0);
        private float _rumbleLow = 0.0f;
        private float _rumbleHigh = 0.0f;

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
                _joyFeedbackArrayMsg = GetLatestMessage();
                foreach(RosJoyFeedback feedback in _joyFeedbackArrayMsg.array)
                {
                    if(feedback.type == RosJoyFeedback.TYPE_LED)
                    {
                        if(feedback.id < 3) _rgb[feedback.id] = feedback.intensity;
                    }
                    else if(feedback.type == RosJoyFeedback.TYPE_RUMBLE)
                    {
                        if(feedback.id == 0) _rumbleLow = feedback.intensity;
                        else if(feedback.id == 1) _rumbleHigh = feedback.intensity;
                    }
                }
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public Color getLatestColor()
        {
            return _rgb;
        }

        public float getLatestRumbleLow()
        {
            return _rumbleLow;
        }

        public float getLatestRumbleHigh()
        {
            return _rumbleHigh;
        }
    }
}