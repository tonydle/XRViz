using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    public class PointCloudRosGPU_PointCloud2 : MonoBehaviour
    {
        // Point cloud subscriber
        [SerializeField] private RosSubscriberPointCloud2 _pointCloudSub;

        // Render material
        [SerializeReference] private Material _renderMaterial;

        // Compute buffer
        ComputeBuffer _pointPositionBuffer;
        ComputeBuffer _pointColorBuffer;
        int _totalNumPoints;

        // Utilities
        bool _isInitialised = false;
        private Bounds _defaultBounds = new Bounds(Vector3.zero, Vector3.one * 1000f);

        void Start()
        {
            // Clone the material to avoid changing the original
            _renderMaterial = new Material(_renderMaterial);
            StartCoroutine(WaitForSubAndInit());
        }

        IEnumerator WaitForSubAndInit()
        {
            // Wait until the point cloud subscriber has received at least one message
            while (!_pointCloudSub.isReady())
            {
                yield return null;
            }

            // Initialize once the point cloud data is ready
            Vector3[] pointPositions = _pointCloudSub.GetLatestPoints();
            Color[] pointColors = _pointCloudSub.GetLatestColors();
            _totalNumPoints = pointPositions.Length;

            _pointPositionBuffer = new ComputeBuffer(_totalNumPoints, sizeof(float) * 3);
            _pointColorBuffer = new ComputeBuffer(_totalNumPoints, sizeof(float) * 4);

            // Set the buffers in the material for rendering
            _renderMaterial.SetBuffer("vertexPosition", _pointPositionBuffer);
            _renderMaterial.SetBuffer("vertexColor", _pointColorBuffer);

            _isInitialised = true;
        }

        void LateUpdate()
        {
            if (!_isInitialised || !_pointCloudSub.isReady())
            {
                return;
            }

            // Get the latest point cloud data
            Vector3[] pointPositions = _pointCloudSub.GetLatestPoints();
            Color[] pointColors = _pointCloudSub.GetLatestColors();
            if (pointPositions.Length != _totalNumPoints || pointColors.Length != _totalNumPoints)
            {
                // Update buffer size if point count changes
                _totalNumPoints = pointPositions.Length;
                _pointPositionBuffer.Release();
                _pointColorBuffer.Release();

                _pointPositionBuffer = new ComputeBuffer(_totalNumPoints, sizeof(float) * 3);
                _pointColorBuffer = new ComputeBuffer(_totalNumPoints, sizeof(float) * 4);
                _renderMaterial.SetBuffer("vertexPosition", _pointPositionBuffer);
                _renderMaterial.SetBuffer("vertexColor", _pointColorBuffer);
            }

            // Update buffer data
            _pointPositionBuffer.SetData(pointPositions);
            _pointColorBuffer.SetData(pointColors);

            // Draw the point cloud
            Graphics.DrawProcedural(_renderMaterial, _defaultBounds, MeshTopology.Points, _totalNumPoints, 1);
        }

        void OnDestroy()
        {
            if (_pointPositionBuffer != null)
            {
                _pointPositionBuffer.Release();
                _pointPositionBuffer = null;
            }

            if (_pointColorBuffer != null)
            {
                _pointColorBuffer.Release();
                _pointColorBuffer = null;
            }
        }
    }
}
