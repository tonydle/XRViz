// RosPublisherJointTrajectory.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosJointTrajectory = RosMessageTypes.Trajectory.JointTrajectoryMsg;
using RosJointTrajectoryPoint = RosMessageTypes.Trajectory.JointTrajectoryPointMsg;
using RosHeader = RosMessageTypes.Std.HeaderMsg;
using RosDuration = RosMessageTypes.BuiltinInterfaces.DurationMsg;
using RosTime = RosMessageTypes.BuiltinInterfaces.TimeMsg;

namespace Unity.Robotics
{
    public class RosPublisherJointTrajectory : RosPublisher<RosJointTrajectory>
    {
        [SerializeField] private RosSubscriberJointState jointStateSubscriber;

        [Tooltip("Frame ID to use in the header")]
        [SerializeField] protected string frameId = "base_link";

        [Header("Trajectory Parameters")]
        [Tooltip("List of joint names for the trajectory")]
        [SerializeField] private List<string> jointNames = new List<string>();

        private List<float> startPositions = new List<float>();

        [Tooltip("End positions for each joint")]
        [SerializeField] private List<float> endPositions = new List<float>();
        [Tooltip("Duration of the trajectory in seconds")]
        [SerializeField] private float duration = 1.0f;

        protected override void Start()
        {
            base.Start();
            _message = new RosJointTrajectory
            {
                header = new RosHeader(),
                joint_names = jointNames.ToArray(),
                points = new RosJointTrajectoryPoint[2]
            };
        }

        // Helper: grab only the subscriber’s positions for the joints you care about
        private List<float> GetFilteredCurrentPositions()
        {
            var state       = jointStateSubscriber.getLatestJointState();
            var subNames    = state.name;
            var subPositions= state.position;
            var filtered    = new List<float>(jointNames.Count);

            for (int i = 0; i < jointNames.Count; i++)
            {
                string name = jointNames[i];
                int idx = subNames.IndexOf(name);
                if (idx >= 0)
                {
                    filtered.Add(subPositions[idx]);
                }
                else
                {
                    Debug.LogWarning($"[RosPublisherJointTrajectory] Joint '{name}' not found in subscriber; defaulting to 0.");
                    filtered.Add(0f);
                }
            }
            return filtered;
        }

        // PublishTrajectoryTo, using filtered start positions
        public void PublishTrajectoryTo(List<float> targetPositions)
        {
            if (!ValidateSubscriber()) return;

            if (targetPositions.Count != jointNames.Count)
            {
                Debug.LogError(
                $"[RosPublisherJointTrajectory] jointNames.Count ({jointNames.Count}) != targetPositions.Count ({targetPositions.Count})");
                return;
            }

            // 1) filter the subscriber’s current positions
            startPositions = GetFilteredCurrentPositions();
            // 2) set your target
            endPositions   = new List<float>(targetPositions);
            // 3) fire off the two-point trajectory
            PublishTrajectory();
        }

        /// <summary>
        /// Publishes a simple two-point trajectory from start to end over the specified duration.
        /// </summary>
        public void PublishTrajectory()
        {
            if (!ValidateSubscriber()) return;
            SetupHeader();
            PopulateJointNames();

            var startPoint = CreatePoint(ConvertFloatToDouble(startPositions), 0f);
            var endPoint   = CreatePoint(ConvertFloatToDouble(endPositions), duration);

            _message.points = new[] { startPoint, endPoint };
            Publish(_message);
        }

        // Helper: ensure subscriber is ready
        private bool ValidateSubscriber()
        {
            if (jointStateSubscriber == null || !jointStateSubscriber.isReady())
            {
                Debug.LogWarning("JointState subscriber not ready");
                return false;
            }
            return true;
        }

        // Helper: set header fields
        private void SetupHeader()
        {
            _message.header.stamp = jointStateSubscriber.GetLatestTime();
            _message.header.frame_id = frameId;
        }

        // Helper: update joint names array
        private void PopulateJointNames()
        {
            _message.joint_names = jointNames.ToArray();
        }

        // Helper: create a trajectory point at given time
        private RosJointTrajectoryPoint CreatePoint(double[] positions, float timeFromStart)
        {
            int sec = Mathf.FloorToInt(timeFromStart);
            uint nsec = (uint)((timeFromStart - sec) * 1e9);
            return new RosJointTrajectoryPoint
            {
                positions = positions,
                time_from_start = new RosDuration { sec = sec, nanosec = nsec }
            };
        }

        // Helper: convert float list to double array
        private double[] ConvertFloatToDouble(List<float> list)
        {
            return list.Select(f => (double)f).ToArray();
        }
    }
}
