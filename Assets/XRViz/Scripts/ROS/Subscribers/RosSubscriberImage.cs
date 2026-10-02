using System;
using UnityEngine;
using RosImage = RosMessageTypes.Sensor.ImageMsg;

namespace Unity.Robotics
{
    // Raw sensor_msgs/Image into a Texture2D, driven by the message's own `encoding` field.
    //
    // Raw rather than CompressedImage because that is what a simulator publishes natively -
    // Gazebo, Isaac and Webots all emit sensor_msgs/Image, and the compressed variants only
    // exist if you additionally run image_transport republisher nodes. It also keeps depth
    // exact: compressedDepth quantises to 16-bit PNG, while a sim's 32FC1 depth is already
    // metric float and there is no reason to throw that away.
    //
    // Both colour and depth come through here; which one you have is decided by the encoding.
    // GetDepthMetersPerUnit() reports what a texel has to be multiplied by to get metres, so a
    // consumer doesn't have to know whether it was handed millimetre uint16 or metric float.
    //
    // ORIENTATION: ROS image data starts at the top-left; Unity's LoadRawTextureData fills from
    // the bottom-left. That means Texture2D texel (x, y) is exactly ROS pixel (x, y) - no flip
    // is needed for reconstruction maths, which is the only thing this is used for here. It does
    // mean the texture renders upside down if you put it straight on a quad; flip it there, not
    // in the upload, so the pixel coordinates keep agreeing with the camera intrinsics.
    public class RosSubscriberImage : RosSubscriber<RosImage>, IRosFrameSource
    {
        private Texture2D _texture2D;
        private bool _ready;
        private float _lastMessageRealtime = -1f;

        // Optical frame of the camera that produced this image, for TfAnchor to place a cloud by
        private string _frameId = string.Empty;

        private string _encoding = string.Empty;
        private int _width;
        private int _height;

        // Texel value -> metres. Combines the texture format's own normalisation with the
        // encoding's unit: R16 samples as [0,1] so it needs x65535 to get back to the raw
        // uint16, then x0.001 because 16UC1 depth is millimetres by ROS convention. RFloat
        // samples as the stored value and 32FC1 is already metres, so both factors are 1.
        private float _depthMetersPerUnit = 1f;

        private bool _isBgr;
        private bool _unsupportedEncodingLogged;

        // Scratch for the row-by-row repack when `step` carries padding. Kept between messages
        // so a padded publisher doesn't allocate a megabyte per frame.
        private byte[] _repackBuffer;

        protected override void Start()
        {
            base.Start();
            _texture2D = new Texture2D(1, 1, TextureFormat.R8, false);
        }

        protected override void Update()
        {
            base.Update();
            if (!NewMessageAvailable())
                return;

            var msg = GetLatestMessage();
            if (msg.data == null || msg.width == 0 || msg.height == 0)
                return;

            if (!TryDescribeEncoding(msg.encoding, out var format, out int bytesPerPixel,
                    out float metersPerUnit, out bool isBgr))
            {
                // Once, not every frame - a bad encoding is a configuration mistake, and at
                // 30 Hz this would otherwise bury every other message in the console
                if (!_unsupportedEncodingLogged)
                {
                    _unsupportedEncodingLogged = true;
                    Debug.LogError($"[XRViz] {name}: unsupported image encoding '{msg.encoding}' on " +
                        $"'{_topic}'. Supported: rgb8, bgr8, rgba8, bgra8, mono8/8UC1, " +
                        "mono16/16UC1, 32FC1.", this);
                }
                return;
            }

            int width = (int)msg.width;
            int height = (int)msg.height;
            int rowBytes = width * bytesPerPixel;
            int expectedBytes = rowBytes * height;

            // `step` is the full row length in bytes and is allowed to exceed width*bpp. Some
            // publishers also just get it wrong, so treat anything under a full row as absent.
            int stride = (int)msg.step;
            if (stride < rowBytes)
                stride = rowBytes;

            if (msg.data.Length < (long)stride * (height - 1) + rowBytes)
            {
                Debug.LogError($"[XRViz] {name}: '{_topic}' says {width}x{height} {msg.encoding} " +
                    $"(step {msg.step}) but carries only {msg.data.Length} bytes. Dropping the " +
                    "frame - uploading it would read past the end of the buffer.", this);
                return;
            }

            // LoadRawTextureData wants exactly the texture's own byte count, tightly packed -
            // it overreads if given less and rejects a mismatch - so anything padded or
            // over-long goes through a repack sized precisely to the image
            byte[] pixels;
            if (stride == rowBytes && msg.data.Length == expectedBytes)
            {
                pixels = msg.data;
            }
            else
            {
                if (_repackBuffer == null || _repackBuffer.Length != expectedBytes)
                    _repackBuffer = new byte[expectedBytes];
                for (int row = 0; row < height; row++)
                    Buffer.BlockCopy(msg.data, row * stride, _repackBuffer, row * rowBytes, rowBytes);
                pixels = _repackBuffer;
            }

            _ready = false;

            if (_texture2D.width != width || _texture2D.height != height || _texture2D.format != format)
                _texture2D.Reinitialize(width, height, format, false);

            _texture2D.LoadRawTextureData(pixels);
            _texture2D.Apply(false, false);

            _encoding = msg.encoding;
            _frameId = msg.header.frame_id;
            _width = width;
            _height = height;
            _depthMetersPerUnit = metersPerUnit;
            _isBgr = isBgr;
            _ready = true;
            _lastMessageRealtime = Time.realtimeSinceStartup;
        }

        // Note 8UC1/16UC1 are the "no interpretation" encodings: OpenCV type names that a driver
        // uses when it declines to say what the channel means. For a depth topic they mean the
        // same as mono8/mono16, which is how they are treated here.
        private static bool TryDescribeEncoding(string encoding, out TextureFormat format,
            out int bytesPerPixel, out float metersPerUnit, out bool isBgr)
        {
            format = TextureFormat.R8;
            bytesPerPixel = 1;
            metersPerUnit = 1f;
            isBgr = false;

            if (string.IsNullOrEmpty(encoding))
                return false;

            switch (encoding.ToLowerInvariant())
            {
                case "rgb8":
                    format = TextureFormat.RGB24; bytesPerPixel = 3; return true;
                case "bgr8":
                    format = TextureFormat.RGB24; bytesPerPixel = 3; isBgr = true; return true;
                case "rgba8":
                    format = TextureFormat.RGBA32; bytesPerPixel = 4; return true;
                case "bgra8":
                    format = TextureFormat.RGBA32; bytesPerPixel = 4; isBgr = true; return true;
                case "mono8":
                case "8uc1":
                    format = TextureFormat.R8; bytesPerPixel = 1; return true;
                case "mono16":
                case "16uc1":
                    // R16 is UNorm: sampling gives [0,1], so x65535 recovers the uint16, and
                    // uint16 depth is millimetres
                    format = TextureFormat.R16; bytesPerPixel = 2; metersPerUnit = 65535f * 0.001f; return true;
                case "32fc1":
                    // Already metres, and RFloat samples the stored value unchanged
                    format = TextureFormat.RFloat; bytesPerPixel = 4; metersPerUnit = 1f; return true;
                default:
                    return false;
            }
        }

        public bool isReady()
        {
            return _ready;
        }

        public Texture2D GetLatestTexture2D()
        {
            return _texture2D;
        }

        public string GetEncoding()
        {
            return _encoding;
        }

        // header.frame_id of the last image - conventionally a *_optical_frame, which is
        // right-down-forward rather than FLU (REP 103/145) and needs the correction TfAnchor
        // applies. Held across ClearData for the same reason as the laser scan's.
        public string FrameId => _frameId;

        public int GetImageWidth()
        {
            return _width;
        }

        public int GetImageHeight()
        {
            return _height;
        }

        // Multiply a sampled texel by this to get metres. 1 for anything that isn't depth.
        public float GetDepthMetersPerUnit()
        {
            return _depthMetersPerUnit;
        }

        // True when the source is bgr8/bgra8 and a consumer has to swap the red and blue
        // channels itself - Unity has no BGR24 texture format to upload into
        public bool IsBgr()
        {
            return _isBgr;
        }

        // Time.realtimeSinceStartup when the last image arrived, or -1 if none has yet
        public float GetLastMessageRealtime()
        {
            return _lastMessageRealtime;
        }

        public void ClearData()
        {
            _ready = false;
            _lastMessageRealtime = -1f;
        }

        protected override void OnTopicChanged()
        {
            ClearData();
            // The new topic may be a different camera at a different resolution or encoding
            _unsupportedEncodingLogged = false;
            _frameId = string.Empty;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_texture2D != null)
                Destroy(_texture2D);
        }
    }
}
