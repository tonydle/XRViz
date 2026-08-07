using UnityEngine;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosLaserScan = RosMessageTypes.Sensor.LaserScanMsg;
using RosMessageTypes.BuiltinInterfaces;

namespace Unity.Robotics
{
    // Turns sensor_msgs/LaserScan polar ranges into Unity-space points in the sensor's own
    // frame, so a visualiser can just parent them under a GameObject and let its Transform
    // place the scan in the room.
    public class RosSubscriberLaserScan : RosSubscriber<RosLaserScan>
    {
        // Reused between messages - a scan arrives at 10-40 Hz and reallocating arrays that
        // often would churn the GC for no reason
        private Vector3[] _points = new Vector3[0];
        private float[] _intensities = new float[0];
        private float[] _ranges = new float[0];
        private int _pointCount;
        private float _rangeMin;
        private float _rangeMax;
        private bool _ready;
        private TimeMsg _latestTime = new TimeMsg();

        // Wall-clock arrival time of the last scan, so a visualiser can age the data out. The
        // header stamp can't do this job: it's the sensor's clock, which need not match Unity's.
        private float _lastMessageRealtime = -1f;

        protected override void Update()
        {
            base.Update();
            if (!NewMessageAvailable())
                return;

            var msg = GetLatestMessage();
            _ready = false;
            _latestTime = msg.header.stamp;
            _rangeMin = msg.range_min;
            _rangeMax = msg.range_max;

            int beamCount = msg.ranges != null ? msg.ranges.Length : 0;
            if (_points.Length < beamCount)
            {
                _points = new Vector3[beamCount];
                _intensities = new float[beamCount];
                _ranges = new float[beamCount];
            }

            bool hasIntensities = msg.intensities != null && msg.intensities.Length == beamCount;

            // Invalid beams are dropped rather than kept as zeroes, so the visualiser never has
            // to know about them - hence compacting into the front of the arrays
            _pointCount = 0;
            for (int i = 0; i < beamCount; i++)
            {
                float range = msg.ranges[i];

                // A no-return beam is reported as NaN or +Inf by most drivers; some report 0 or
                // a value outside [range_min, range_max] instead
                if (float.IsNaN(range) || float.IsInfinity(range) ||
                    range < msg.range_min || range > msg.range_max)
                    continue;

                float angle = msg.angle_min + i * msg.angle_increment;

                // LaserScan is polar in the sensor frame, which is ROS FLU: +x forward,
                // +y left, angle measured counter-clockwise about +z (up)
                var ros = new Vector3(range * Mathf.Cos(angle), range * Mathf.Sin(angle), 0f);

                _points[_pointCount] = FLU.ConvertToRUF(ros);
                _ranges[_pointCount] = range;
                _intensities[_pointCount] = hasIntensities ? msg.intensities[i] : 0f;
                _pointCount++;
            }

            _ready = true;
            _lastMessageRealtime = Time.realtimeSinceStartup;
        }

        protected override void OnTopicChanged()
        {
            ClearData();
        }

        // Drop everything parsed from the current topic. isReady() goes false until the next
        // message arrives, so a visualiser stops drawing instead of redrawing the last sweep
        // forever - the panel's "Clear Scan" button and the staleness timeout both come here.
        public void ClearData()
        {
            _ready = false;
            _pointCount = 0;
            _lastMessageRealtime = -1f;
        }

        // Time.realtimeSinceStartup when the last scan arrived, or -1 if none has yet
        public float GetLastMessageRealtime()
        {
            return _lastMessageRealtime;
        }

        public bool isReady()
        {
            return _ready;
        }

        // Valid points only, in Unity coordinates, local to the sensor frame. Read the first
        // GetPointCount() entries - the arrays are longer and the tail is stale.
        public Vector3[] GetPoints()
        {
            return _points;
        }

        public float[] GetRanges()
        {
            return _ranges;
        }

        public float[] GetIntensities()
        {
            return _intensities;
        }

        public int GetPointCount()
        {
            return _pointCount;
        }

        public float GetRangeMin()
        {
            return _rangeMin;
        }

        public float GetRangeMax()
        {
            return _rangeMax;
        }

        public TimeMsg GetLatestTime()
        {
            return _latestTime;
        }
    }
}
