using System.Collections;
using UnityEngine;

namespace Unity.Robotics
{
    public class PointCloudRosGPU_PointCloud2 : MonoBehaviour
    {
        // Point cloud subscriber
        [SerializeField] private RosSubscriberPointCloud2 m_pointCloudSub;

        // Render material
        [SerializeReference] private Material m_renderMaterial;

        // Compute buffer
        private ComputeBuffer m_pointPositionBuffer;
        private ComputeBuffer m_pointColorBuffer;
        private int m_totalNumPoints;

        // Utilities
        private bool m_isInitialised = false;
        private Bounds m_defaultBounds = new(Vector3.zero, Vector3.one * 1000f);

        private void Start()
        {
            // Clone the material to avoid changing the original
            m_renderMaterial = new Material(m_renderMaterial);
            _ = StartCoroutine(WaitForSubAndInit());
        }

        private IEnumerator WaitForSubAndInit()
        {
            // Wait until the point cloud subscriber has received at least one message
            while (!m_pointCloudSub.isReady())
            {
                yield return null;
            }

            // Initialize once the point cloud data is ready
            var pointPositions = m_pointCloudSub.GetLatestPoints();
            m_totalNumPoints = pointPositions.Length;

            m_pointPositionBuffer = new ComputeBuffer(m_totalNumPoints, sizeof(float) * 3);
            m_pointColorBuffer = new ComputeBuffer(m_totalNumPoints, sizeof(float) * 4);

            // Set the buffers in the material for rendering
            m_renderMaterial.SetBuffer("vertexPosition", m_pointPositionBuffer);
            m_renderMaterial.SetBuffer("vertexColor", m_pointColorBuffer);

            m_isInitialised = true;
        }

        private void LateUpdate()
        {
            if (!m_isInitialised || !m_pointCloudSub.isReady())
            {
                return;
            }

            // Get the latest point cloud data
            var pointPositions = m_pointCloudSub.GetLatestPoints();
            var pointColors = m_pointCloudSub.GetLatestColors();
            if (pointPositions.Length != m_totalNumPoints || pointColors.Length != m_totalNumPoints)
            {
                // Update buffer size if point count changes
                m_totalNumPoints = pointPositions.Length;
                m_pointPositionBuffer.Release();
                m_pointColorBuffer.Release();

                m_pointPositionBuffer = new ComputeBuffer(m_totalNumPoints, sizeof(float) * 3);
                m_pointColorBuffer = new ComputeBuffer(m_totalNumPoints, sizeof(float) * 4);
                m_renderMaterial.SetBuffer("vertexPosition", m_pointPositionBuffer);
                m_renderMaterial.SetBuffer("vertexColor", m_pointColorBuffer);
            }

            // Update buffer data
            m_pointPositionBuffer.SetData(pointPositions);
            m_pointColorBuffer.SetData(pointColors);

            // Draw the point cloud
            Graphics.DrawProcedural(m_renderMaterial, m_defaultBounds, MeshTopology.Points, m_totalNumPoints, 1);
        }

        private void OnDestroy()
        {
            if (m_pointPositionBuffer != null)
            {
                m_pointPositionBuffer.Release();
                m_pointPositionBuffer = null;
            }

            if (m_pointColorBuffer != null)
            {
                m_pointColorBuffer.Release();
                m_pointColorBuffer = null;
            }
        }
    }
}
