using System;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosPointCloud2 = RosMessageTypes.Sensor.PointCloud2Msg;

namespace Unity.Robotics
{
    // Turns sensor_msgs/PointCloud2 into Unity-space points in the sensor's own frame, so a
    // visualiser can parent them under a GameObject and let its Transform place the cloud in the
    // room - the same contract RosSubscriberLaserScan offers.
    //
    // What the previous version of this file got wrong, all of which showed up as a cloud that
    // looked plausible and was wrong:
    //   - NO COORDINATE CONVERSION. ROS is right-handed Z-up, Unity left-handed Y-up, so the raw
    //     (x,y,z) landed the cloud rotated and mirrored (this is the exception CLAUDE.md calls
    //     out). Both conventions are handled properly below.
    //   - It assumed an "rgb" field existed and threw from GetFieldOffset if it didn't, which is
    //     every lidar cloud ever published.
    //   - It parsed every point of every message on the main thread. A 640x480 organised cloud is
    //     307200 points; decimation now happens BEFORE the parse, so the cost is bounded by
    //     _maxPoints rather than by whatever the sensor sends.
    //   - It kept invalid points. A non-dense cloud is padded with NaNs, and a NaN vertex poisons
    //     the mesh bounds, which makes the whole cloud vanish under frustum culling.
    public class RosSubscriberPointCloud2 : RosSubscriber<RosPointCloud2>, IRosFrameSource
    {
        // sensor_msgs/PointField datatype constants
        private const byte k_Int8 = 1, k_Uint8 = 2, k_Int16 = 3, k_Uint16 = 4;
        private const byte k_Int32 = 5, k_Uint32 = 6, k_Float32 = 7, k_Float64 = 8;

        public enum ColorMode
        {
            // The cloud's own rgb/rgba where it has one, a range gradient where it doesn't -
            // which covers an RGBD camera and a lidar with the same setting
            Auto,
            Rgb,
            Range,
            Intensity,
            Flat,
        }

        public enum OpticalFrameHandling
        {
            // Frames named *_optical_frame are right-down-forward; everything else is FLU
            Auto,
            Never,
            Always,
        }

        [Tooltip("Most points to keep from one message. The cloud is strided down to this before " +
                 "anything is parsed, so it caps CPU cost as well as triangle count. The real " +
                 "limit is usually the TCP socket, not this - decimate on the ROS side too.")]
        [SerializeField] private int _maxPoints = 30000;

        [Tooltip("Keep every Nth point regardless of _maxPoints. 1 keeps everything the point " +
                 "budget allows.")]
        [SerializeField] private int _decimation = 1;

        [Tooltip("Drop points closer than this to the sensor, in metres. 0 keeps everything.")]
        [SerializeField] private float _minRange = 0f;

        [Tooltip("Drop points further than this from the sensor, in metres. 0 keeps everything.")]
        [SerializeField] private float _maxRange = 8f;

        [SerializeField] private ColorMode _colorMode = ColorMode.Auto;

        [Tooltip("Intensity that maps to the far end of the gradient. The message gives " +
                 "intensity no units and no range, so this has to be told.")]
        [SerializeField] private float _intensityMax = 255f;

        [Tooltip("Gradient used for range and intensity colouring - i.e. whenever the cloud " +
                 "carries no colour of its own.")]
        [SerializeField] private Color _nearColor = new Color(0.25f, 1f, 0.5f);
        [SerializeField] private Color _farColor = new Color(0.2f, 0.45f, 1f);

        [Tooltip("A camera optical frame is right-down-forward (REP 103/145), not FLU, so its " +
                 "conversion is a plain Y flip rather than the FLU permutation. Auto decides on " +
                 "the frame name, matching TfAnchor - so the cloud and the transform that places " +
                 "it always agree about which convention they are in.")]
        [SerializeField] private OpticalFrameHandling _opticalFrame = OpticalFrameHandling.Auto;

        // Capacity-sized, valid prefix only - read the first GetPointCount() entries
        private Vector3[] _points = new Vector3[0];
        private Color[] _colors = new Color[0];
        private int _pointCount;

        private bool _ready;
        private string _frameId = string.Empty;
        private float _lastMessageRealtime = -1f;
        private bool _bigEndianLogged;

        public string FrameId => _frameId;

        public int GetPointCount() => _pointCount;

        // Time.realtimeSinceStartup when the last cloud arrived, or -1 if none has. Arrival time
        // rather than the header stamp: the header is the sensor's clock, which need not agree
        // with Unity's.
        public float GetLastMessageRealtime() => _lastMessageRealtime;

        public bool isReady() => _ready;

        public Vector3[] GetLatestPoints() => _points;

        public Color[] GetLatestColors() => _colors;

        protected override void Update()
        {
            base.Update();
            if (!NewMessageAvailable())
                return;

            Parse(GetLatestMessage());
        }

        protected override void OnTopicChanged()
        {
            ClearData();
            // A different topic is a different sensor, so its frame no longer applies
            _frameId = string.Empty;
        }

        // Drop everything parsed from the current topic. isReady() goes false until the next
        // message arrives, so a visualiser stops drawing rather than redrawing the last cloud
        // forever - the panel's Clear Data button and the staleness timeout both come here.
        public void ClearData()
        {
            _ready = false;
            _pointCount = 0;
            _lastMessageRealtime = -1f;
        }

        private void Parse(RosPointCloud2 msg)
        {
            _ready = false;

            if (msg?.data == null || msg.fields == null)
                return;

            _frameId = msg.header.frame_id;

            // Everything below reads little-endian. Rather than produce a confidently wrong cloud
            // from byte-swapped floats, say so and draw nothing.
            if (msg.is_bigendian)
            {
                if (!_bigEndianLogged)
                {
                    _bigEndianLogged = true;
                    Debug.LogError($"[XRViz] '{_topic}' is big-endian, which this subscriber does " +
                        "not decode. Republish it little-endian.", this);
                }
                return;
            }

            int pointStep = (int)msg.point_step;
            int total = (int)msg.width * (int)msg.height;
            if (pointStep <= 0 || total <= 0)
                return;

            // Guard against a truncated payload rather than reading off the end of the array
            total = Mathf.Min(total, msg.data.Length / pointStep);

            int offsetX = FieldOffset(msg, "x", k_Float32);
            int offsetY = FieldOffset(msg, "y", k_Float32);
            int offsetZ = FieldOffset(msg, "z", k_Float32);
            if (offsetX < 0 || offsetY < 0 || offsetZ < 0)
            {
                Debug.LogWarning($"[XRViz] '{_topic}' has no float32 x/y/z fields; nothing to draw.", this);
                return;
            }

            // "rgb" is conventionally declared FLOAT32 even though the bits are a packed uint32,
            // so the datatype is not checked - only that the field exists and has four bytes
            int offsetRgb = FieldOffset(msg, "rgba", 0);
            if (offsetRgb < 0)
                offsetRgb = FieldOffset(msg, "rgb", 0);
            int offsetIntensity = FieldOffset(msg, "intensity", 0);
            byte intensityType = offsetIntensity >= 0 ? FieldType(msg, "intensity") : (byte)0;

            // Resolve the colour source once here rather than re-deciding per point. A mode
            // that asks for a field the cloud hasn't got falls back to range, which every cloud
            // can always produce - drawing nothing because the rgb field is missing would be a
            // worse answer than drawing it in the wrong colours.
            ColorMode mode = _colorMode;
            if (mode == ColorMode.Auto)
                mode = offsetRgb >= 0 ? ColorMode.Rgb : ColorMode.Range;
            if (mode == ColorMode.Rgb && offsetRgb < 0)
                mode = ColorMode.Range;
            if (mode == ColorMode.Intensity && offsetIntensity < 0)
                mode = ColorMode.Range;

            // Stride the cloud down BEFORE parsing it - this is what keeps a 307200-point
            // organised cloud off the main thread's critical path
            int budget = Mathf.Max(1, _maxPoints);
            int stride = Mathf.Max(1, _decimation);
            if (total / stride > budget)
                stride = Mathf.CeilToInt(total / (float)budget);

            int capacity = total / stride + 1;
            if (_points.Length < capacity)
            {
                _points = new Vector3[capacity];
                _colors = new Color[capacity];
            }

            bool optical = UseOpticalConvention(_frameId);
            float minRangeSq = _minRange > 0f ? _minRange * _minRange : -1f;
            float maxRangeSq = _maxRange > 0f ? _maxRange * _maxRange : -1f;
            float rangeSpan = Mathf.Max(_maxRange - _minRange, 0.001f);

            int count = 0;
            for (int i = 0; i < total; i += stride)
            {
                int at = i * pointStep;

                float x = BitConverter.ToSingle(msg.data, at + offsetX);
                float y = BitConverter.ToSingle(msg.data, at + offsetY);
                float z = BitConverter.ToSingle(msg.data, at + offsetZ);

                // A non-dense cloud pads its gaps with NaN or +/-Inf. One of those in the mesh
                // poisons its bounds, and a NaN bounding box culls the entire cloud - so this
                // check is not politeness, it is the difference between drawing and not.
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                    float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                    continue;

                float rangeSq = x * x + y * y + z * z;
                if (minRangeSq > 0f && rangeSq < minRangeSq)
                    continue;
                if (maxRangeSq > 0f && rangeSq > maxRangeSq)
                    continue;

                // The conversion, and the thing about this file easiest to get wrong. An
                // optical frame is right-down-forward - already Unity's axis layout bar the Y
                // direction - so it is a plain Y flip. Anything else is FLU, and takes the
                // project's shared conversion rather than a hand-rolled permutation.
                _points[count] = optical
                    ? new Vector3(x, -y, z)
                    : FLU.ConvertToRUF(new Vector3(x, y, z));

                switch (mode)
                {
                    case ColorMode.Rgb:
                    {
                        // Conventionally declared FLOAT32 even though the bits are a packed
                        // uint32, so the same four bytes are read as an integer either way
                        uint packed = BitConverter.ToUInt32(msg.data, at + offsetRgb);
                        _colors[count] = new Color(
                            ((packed >> 16) & 0xFF) / 255f,
                            ((packed >> 8) & 0xFF) / 255f,
                            (packed & 0xFF) / 255f);
                        break;
                    }
                    case ColorMode.Intensity:
                    {
                        float intensity = ReadScalar(msg.data, at + offsetIntensity, intensityType);
                        _colors[count] = Color.Lerp(_nearColor, _farColor,
                            Mathf.Clamp01(intensity / Mathf.Max(_intensityMax, 0.001f)));
                        break;
                    }
                    case ColorMode.Flat:
                        _colors[count] = _nearColor;
                        break;
                    default:
                    {
                        float range = Mathf.Sqrt(rangeSq);
                        _colors[count] = Color.Lerp(_nearColor, _farColor,
                            Mathf.Clamp01((range - _minRange) / rangeSpan));
                        break;
                    }
                }

                count++;
            }

            _pointCount = count;
            _ready = count > 0;
            _lastMessageRealtime = Time.realtimeSinceStartup;
        }

        // Matches TfAnchor.UseOpticalCorrection, deliberately: the cloud's convention and the
        // convention of the transform that places it have to be decided the same way, or the
        // cloud lands on its side relative to its own frame.
        private bool UseOpticalConvention(string frame)
        {
            switch (_opticalFrame)
            {
                case OpticalFrameHandling.Always:
                    return true;
                case OpticalFrameHandling.Never:
                    return false;
                default:
                    return !string.IsNullOrEmpty(frame) &&
                        frame.EndsWith("_optical_frame", StringComparison.Ordinal);
            }
        }

        // Byte offset of a named field, or -1 when the cloud hasn't got one. requiredType 0 means
        // any type will do.
        private static int FieldOffset(RosPointCloud2 msg, string name, byte requiredType)
        {
            foreach (var field in msg.fields)
            {
                if (field.name != name)
                    continue;
                if (requiredType != 0 && field.datatype != requiredType)
                    return -1;
                return (int)field.offset;
            }
            return -1;
        }

        private static byte FieldType(RosPointCloud2 msg, string name)
        {
            foreach (var field in msg.fields)
            {
                if (field.name == name)
                    return field.datatype;
            }
            return 0;
        }

        private static float ReadScalar(byte[] data, int at, byte datatype)
        {
            switch (datatype)
            {
                case k_Int8: return (sbyte)data[at];
                case k_Uint8: return data[at];
                case k_Int16: return BitConverter.ToInt16(data, at);
                case k_Uint16: return BitConverter.ToUInt16(data, at);
                case k_Int32: return BitConverter.ToInt32(data, at);
                case k_Uint32: return BitConverter.ToUInt32(data, at);
                case k_Float32: return BitConverter.ToSingle(data, at);
                case k_Float64: return (float)BitConverter.ToDouble(data, at);
                default: return 0f;
            }
        }
    }
}
