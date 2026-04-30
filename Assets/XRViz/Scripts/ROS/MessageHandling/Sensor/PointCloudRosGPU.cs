using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;


namespace Unity.Robotics
{
    public class PointCloudRosGPU : MonoBehaviour
    {
        // To be linked in Unity Editor
        [SerializeField] private RosSubscriberCameraInfo _camInfoColorSub;
        [SerializeField] private RosSubscriberCameraInfo _camInfoDepthSub;
        [SerializeField] private RosSubscriberCompressedImage _imageColorSub;
        [SerializeField] private RosSubscriberCompressedImage _imageDepthLowerSub;
        [SerializeField] private RosSubscriberCompressedImage _imageDepthUpperSub;

        // Render Material
        [SerializeReference] private Material _renderMaterial;

        // Compute Shader related - GPU
        public ComputeShader _computeShader;

        // Compute buffers
        ComputeBuffer _depthComputeBuffer;
        ComputeBuffer _colorComputeBuffer;
        ComputeBuffer _distanceComputeBuffer;
        int _totalNumVertices;

        // GPU Kernel ID
        int _kernelHandleDepth = 0;

        // Utilities
        bool _isInitialised = false;

        // To be subscribed from ROS
        private float[] _camInfoDepth;
        private uint _colorImageWidth, _colorImageHeight;
        private Texture2D _colorTexture;
        private Texture2D _depthLowerTexture;
        private Texture2D _depthUpperTexture;
        private Transform _pointCloudOriginTransform;
        private Bounds _defaultBounds = new Bounds(Vector3.zero, Vector3.one*1000f);

        void Start()
        {
            _renderMaterial = new Material(_renderMaterial);
            _computeShader = Instantiate(_computeShader);
            StartCoroutine(WaitForSubsAndInit());
        }

        IEnumerator WaitForSubsAndInit()
        {
            // Wait until the necessary subscribers have received at least one message
            while (!_camInfoColorSub.isReady() || !_camInfoDepthSub.isReady())
            {
                yield return null;
            }

            // Now that the components are ready, perform the initialization
            _camInfoDepth = _camInfoDepthSub.GetCameraNecessaryInfo();
            _colorImageWidth = _camInfoColorSub.GetImageWidth();
            _colorImageHeight = _camInfoColorSub.GetImageHeight();
            _pointCloudOriginTransform = GameObject.Find(_camInfoDepthSub.GetFrameId()).transform;

            // Initialize the compute shader's static variables and buffers
            _totalNumVertices = (int)_colorImageWidth * (int)_colorImageHeight;

            _depthComputeBuffer = new ComputeBuffer(_totalNumVertices, sizeof(float)*3);
            _colorComputeBuffer = new ComputeBuffer(_totalNumVertices, sizeof(float)*4);
            _distanceComputeBuffer = new ComputeBuffer(_totalNumVertices, sizeof(float));

            _computeShader.SetFloats("camInfoDepth",_camInfoDepth);
            _computeShader.SetInt("colorImageWidth",(int)_colorImageWidth);
            _computeShader.SetInt("colorImageHeight",(int)_colorImageHeight);

            // Set outputs for GPU kernels
            _computeShader.SetBuffer(_kernelHandleDepth,"depthOut",_depthComputeBuffer);
            _computeShader.SetBuffer(_kernelHandleDepth,"colorOut",_colorComputeBuffer);
            _computeShader.SetBuffer(_kernelHandleDepth,"distanceToOrigin",_distanceComputeBuffer);

            // Set values to Material for rendering
            _renderMaterial.SetBuffer("vertexPosition",_depthComputeBuffer);
            _renderMaterial.SetBuffer("vertexColor",_colorComputeBuffer);
            _renderMaterial.SetBuffer("distanceToOrigin",_distanceComputeBuffer);

            _isInitialised = true;
        }

        private void LateUpdate()
        {
            if(!_isInitialised)
            {
                return;
            }

            // make sure to have enough images before processing
            uint numImages = 0;
            if(_imageColorSub.isReady())
            {
                _colorTexture = _imageColorSub.GetLatestTexture2D();
                numImages++;
            }
            if(_imageDepthLowerSub.isReady())
            {
                _depthLowerTexture = _imageDepthLowerSub.GetLatestTexture2D();
                numImages++;
            }
            if(_imageDepthUpperSub.isReady())
            {
                _depthUpperTexture = _imageDepthUpperSub.GetLatestTexture2D();
                numImages++;
            }
            if(numImages == 3)
            {
                GPUGeneratePointCloud();                
            }
        }

        void GPUGeneratePointCloud()
        {
           // Set inputs for GPU kernels
            _computeShader.SetTexture(_kernelHandleDepth,"inColor",_colorTexture);
            _computeShader.SetTexture(_kernelHandleDepth,"inDepthLower",_depthLowerTexture);
            _computeShader.SetTexture(_kernelHandleDepth,"inDepthUpper",_depthUpperTexture);

            // Set camera origin matrix
            _computeShader.SetMatrix("originTransform",_pointCloudOriginTransform.localToWorldMatrix);
            
            // Dispatch to invoke GPU computing
            _computeShader.Dispatch(_kernelHandleDepth,(int)_colorImageWidth/8,(int)_colorImageHeight/8,1);

            Graphics.DrawProcedural(_renderMaterial, _defaultBounds, MeshTopology.Points, _totalNumVertices, 1);
        }

        void OnDestroy()
        {
            if (_depthComputeBuffer != null)
            {
                _depthComputeBuffer.Release();
                _depthComputeBuffer = null;
            }

            if (_colorComputeBuffer != null)
            {
                _colorComputeBuffer.Release();
                _colorComputeBuffer = null;
            }

            if (_distanceComputeBuffer != null)
            {
                _distanceComputeBuffer.Release();
                _distanceComputeBuffer = null;
            }
        }
    }
}