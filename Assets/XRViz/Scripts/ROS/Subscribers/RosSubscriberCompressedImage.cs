using UnityEngine;
using LightBuzz.Jpeg;
using RosCompressedImage = RosMessageTypes.Sensor.CompressedImageMsg;

namespace Unity.Robotics
{
    public class RosSubscriberCompressedImage : RosSubscriber<RosCompressedImage>
    {
        private Texture2D _texture2D;
        private TextureFormat _textureFormat = TextureFormat.RGB24;
        private RosCompressedImage _msg;
        private JpegDecoder _jpegDecoder;
        private bool _ready = false;

        protected override void Start()
        {
            base.Start();
            _texture2D = new Texture2D(1, 1, _textureFormat, false);
            _jpegDecoder = new JpegDecoder();
        }

        protected override void Update()
        {
            base.Update();
            if (NewMessageAvailable())
            {
                _ready = false;
                _msg = GetLatestMessage();

                // determine whether the image colour format
                if (_msg.format.Contains("rgb8"))
                {
                    _textureFormat = TextureFormat.RGB24;
                }
                else if (_msg.format.Contains("16UC1"))
                {
                    _textureFormat = TextureFormat.R16;
                }
                else
                {
                    Debug.LogError("Unsupported image format: " + _msg.format);
                    return;
                }

                if (_msg.format.Contains("jpeg"))
                {
                    var rawData = _jpegDecoder.Decode(_msg.data, PixelFormat.RGB, Flag.NONE, out var width, out var height);
                    if (_texture2D.width != width || _texture2D.height != height)
                    {
                        _texture2D.Reinitialize(width, height, _textureFormat, false);
                    }
                    _texture2D.LoadRawTextureData(rawData);
                    _texture2D.Apply();
                }
                else if (_msg.format.Contains("png") || _msg.format.Contains("compressedDepth"))
                {
                    if (!CompressedDepthPNGDecoder.TryExtractPngPayload(_msg.data, out var pngData, out var pngStartOffset))
                    {
                        Debug.LogError("PNG decode failed: could not find PNG signature in compressed image payload. format=" + _msg.format);
                        return;
                    }

                    if (_msg.format.Contains("16UC1"))
                    {
                        if (!CompressedDepthPNGDecoder.TryDecode16Uc1PngToR16(pngData, out var width, out var height, out var rawDepthBytes))
                        {
                            Debug.LogError("16UC1 PNG decode failed. format=" + _msg.format + " payloadBytes=" + pngData.Length + " pngStartOffset=" + pngStartOffset);
                            return;
                        }

                        if (_texture2D.width != width || _texture2D.height != height || _texture2D.format != TextureFormat.R16)
                        {
                            _texture2D.Reinitialize(width, height, _textureFormat, false);
                        }
                        _texture2D.LoadRawTextureData(rawDepthBytes);
                        _texture2D.Apply();
                    }
                    else
                    {
                        _texture2D.Reinitialize(2, 2, _textureFormat, false);
                        if (!ImageConversion.LoadImage(_texture2D, pngData))
                        {
                            Debug.LogError("PNG decode failed for compressed image. format=" + _msg.format + " payloadBytes=" + pngData.Length);
                            return;
                        }
                    }
                }
                _ready = true;
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

        protected void OnDestroy()
        {
            if (_texture2D != null)
            {
                Destroy(_texture2D);
            }
        }

    }
}
