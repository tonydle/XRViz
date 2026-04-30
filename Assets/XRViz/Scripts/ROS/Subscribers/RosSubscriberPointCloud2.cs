using UnityEngine;
using RosPointCloud2 = RosMessageTypes.Sensor.PointCloud2Msg;
using System;

namespace Unity.Robotics
{
    public class RosSubscriberPointCloud2 : RosSubscriber<RosPointCloud2>
    {
        private Vector3[] _points;
        private Color[] _colors;
        private RosPointCloud2 _msg;
        private bool _ready = false;

        protected override void Start()
        {
            base.Start();
            _points = new Vector3[0];
            _colors = new Color[0];
        }

        protected override void Update()
        {
            base.Update();
            if (NewMessageAvailable())
            {
                _ready = false;
                _msg = GetLatestMessage();
                int numPoints = (int)_msg.width * (int)_msg.height;

                if (_points.Length != numPoints)
                {
                    _points = new Vector3[numPoints];
                    _colors = new Color[numPoints];
                }

                int pointStep = (int)_msg.point_step;
                int offsetX = GetFieldOffset(_msg, "x");
                int offsetY = GetFieldOffset(_msg, "y");
                int offsetZ = GetFieldOffset(_msg, "z");
                int offsetRGB = GetFieldOffset(_msg, "rgb");

                for (int i = 0; i < numPoints; i++)
                {
                    int pointOffset = i * pointStep;
                    _points[i] = new Vector3(
                        BitConverter.ToSingle(_msg.data, pointOffset + offsetX),
                        BitConverter.ToSingle(_msg.data, pointOffset + offsetY),
                        BitConverter.ToSingle(_msg.data, pointOffset + offsetZ));

                    uint rgb = BitConverter.ToUInt32(_msg.data, pointOffset + offsetRGB);
                    byte r = (byte)((rgb >> 16) & 0xFF);
                    byte g = (byte)((rgb >> 8) & 0xFF);
                    byte b = (byte)(rgb & 0xFF);

                    _colors[i] = new Color(r / 255.0f, g / 255.0f, b / 255.0f);
                }
                
                _ready = true;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public Vector3[] GetLatestPoints()
        {
            return _points;
        }

        public Color[] GetLatestColors()
        {
            return _colors;
        }

        private int GetFieldOffset(RosPointCloud2 msg, string fieldName)
        {
            foreach (var field in msg.fields)
            {
                if (field.name == fieldName)
                {
                    return (int)field.offset;
                }
            }
            throw new System.Exception("Field not found: " + fieldName);
        }
    }
}
