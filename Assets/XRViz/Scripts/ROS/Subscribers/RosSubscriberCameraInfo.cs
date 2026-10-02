using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System.Collections.Generic;
using RosCameraInfo = RosMessageTypes.Sensor.CameraInfoMsg;

namespace Unity.Robotics
{
    public class RosSubscriberCameraInfo : RosSubscriber<RosCameraInfo>, IRosFrameSource
    {
        private string _frame_id = "";
        private float[] _necessaryInfo = new float[4];
        private uint _width;
        private uint _height;
        private RosCameraInfo _camInfo;
        private bool _ready = false;

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
                _camInfo = GetLatestMessage();
                _necessaryInfo[0] = (float)_camInfo.p[2];
                _necessaryInfo[1] = (float)_camInfo.p[6];
                _necessaryInfo[2] = (float)_camInfo.p[0];
                _necessaryInfo[3] = (float)_camInfo.p[5];
                _width = _camInfo.width;
                _height = _camInfo.height;
                _frame_id = _camInfo.header.frame_id;
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public uint GetImageWidth()
        {
            return _width;
        }

        public uint GetImageHeight()
        {
            return _height;
        }

        public float[] GetCameraNecessaryInfo()
        {
            return _necessaryInfo;
        }
        
        public string GetFrameId()
        {
            return _frame_id;
        }

        // Same value under the interface TfAnchor consumes; GetFrameId stays for existing callers
        public string FrameId => _frame_id;
    }
}