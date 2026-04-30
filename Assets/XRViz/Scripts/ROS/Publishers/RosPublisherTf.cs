using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosTFMessage = RosMessageTypes.Tf2.TFMessageMsg;
using RosTransformStamped = RosMessageTypes.Geometry.TransformStampedMsg;
using RosTransform = RosMessageTypes.Geometry.TransformMsg;

namespace Unity.Robotics
{
    public class RosPublisherTF : RosPublisher<RosTFMessage>
    {
        [Tooltip("List of transforms to publish to /tf")]
        [SerializeField] private List<Transform> _objects = new List<Transform>();

        [Tooltip("Optional parent frames (same order as objects); can be null")]
        [SerializeField] private List<Transform> _parentFrames = new List<Transform>();

        [SerializeField] private bool _autoPublish = true;

        protected override void Start()
        {
            base.Start();
            _message = new RosTFMessage();
        }

        private void Update()
        {
            if (_autoPublish)
                Publish();
        }

        public void Publish()
        {
            var transforms = new List<RosTransformStamped>();
            DateTime now = DateTime.UtcNow;
            long epochMilliseconds = (long)(now - new DateTime(1970, 1, 1)).TotalMilliseconds;
            int sec = (int)(epochMilliseconds / 1000);
            uint nanosec = (uint)((epochMilliseconds % 1000) * 1000000);

            for (int i = 0; i < _objects.Count; i++)
            {
                Transform obj = _objects[i];
                if (obj == null) continue;

                string parentFrame = "world";
                Vector3 position = obj.position;
                Quaternion rotation = obj.rotation;

                if (i < _parentFrames.Count && _parentFrames[i] != null)
                {
                    Transform frame = _parentFrames[i];
                    parentFrame = frame.name;
                    position = frame.InverseTransformPoint(obj.position);
                    rotation = Quaternion.Inverse(frame.rotation) * obj.rotation;
                }

                RosTransformStamped tfStamped = new RosTransformStamped
                {
                    header = new RosMessageTypes.Std.HeaderMsg
                    {
                        frame_id = parentFrame,
                        stamp = new RosMessageTypes.BuiltinInterfaces.TimeMsg { sec = sec, nanosec = nanosec }
                    },
                    child_frame_id = obj.name,
                    transform = new RosTransform
                    {
                        translation = position.To<FLU>(),
                        rotation = rotation.To<FLU>()
                    }
                };

                transforms.Add(tfStamped);
            }

            _message.transforms = transforms.ToArray();
            Publish(_message);
        }
    }
}
