using System.Collections;
using UnityEngine;


namespace Unity.Robotics
{
    public class PointCloudRosGPU : MonoBehaviour
    {
        // To be linked in Unity Editor
        [SerializeField] private RosSubscriberCameraInfo m_camInfoColorSub;
        [SerializeField] private RosSubscriberCameraInfo m_camInfoDepthSub;
        [SerializeField] private RosSubscriberCompressedImage m_imageColorSub;
        [SerializeField] private RosSubscriberCompressedImage m_imageDepthSub;

        // Render Material
        [SerializeReference] private Material m_renderMaterial;

        // Compute Shader related - GPU
        public ComputeShader ComputeShader;

        // Compute buffers
        private ComputeBuffer m_depthComputeBuffer;
        private ComputeBuffer m_colorComputeBuffer;
        private ComputeBuffer m_distanceComputeBuffer;
        private int m_totalNumVertices;

        // GPU Kernel ID
        private int m_kernelHandleDepth = 0;

        // Utilities
        private bool m_isInitialised = false;

        // To be subscribed from ROS
        private float[] m_camInfoDepth;
        private uint m_colorImageWidth, m_colorImageHeight;
        private Texture2D m_colorTexture;
        private Texture2D m_depthTexture;
        private Transform m_pointCloudOriginTransform;
        private Bounds m_defaultBounds = new(Vector3.zero, Vector3.one * 1000f);

        private void Start()
        {
            m_renderMaterial = new Material(m_renderMaterial);
            ComputeShader = Instantiate(ComputeShader);
            StartCoroutine(WaitForSubsAndInit());
        }

        private IEnumerator WaitForSubsAndInit()
        {
            // Wait until the necessary subscribers have received at least one message
            while (!m_camInfoColorSub.isReady() || !m_camInfoDepthSub.isReady())
            {
                yield return null;
            }

            // Now that the components are ready, perform the initialization
            m_camInfoDepth = m_camInfoDepthSub.GetCameraNecessaryInfo();
            m_colorImageWidth = m_camInfoColorSub.GetImageWidth();
            m_colorImageHeight = m_camInfoColorSub.GetImageHeight();
            // m_pointCloudOriginTransform = GameObject.Find(m_camInfoDepthSub.GetFrameId()).transform;
            m_pointCloudOriginTransform = transform;

            // Initialize the compute shader's static variables and buffers
            m_totalNumVertices = (int)m_colorImageWidth * (int)m_colorImageHeight;

            m_depthComputeBuffer = new ComputeBuffer(m_totalNumVertices, sizeof(float) * 3);
            m_colorComputeBuffer = new ComputeBuffer(m_totalNumVertices, sizeof(float) * 4);
            m_distanceComputeBuffer = new ComputeBuffer(m_totalNumVertices, sizeof(float));

            ComputeShader.SetFloats("camInfoDepth", m_camInfoDepth);
            ComputeShader.SetInt("colorImageWidth", (int)m_colorImageWidth);
            ComputeShader.SetInt("colorImageHeight", (int)m_colorImageHeight);

            // Set outputs for GPU kernels
            ComputeShader.SetBuffer(m_kernelHandleDepth, "depthOut", m_depthComputeBuffer);
            ComputeShader.SetBuffer(m_kernelHandleDepth, "colorOut", m_colorComputeBuffer);
            ComputeShader.SetBuffer(m_kernelHandleDepth, "distanceToOrigin", m_distanceComputeBuffer);

            // Set values to Material for rendering
            m_renderMaterial.SetBuffer("vertexPosition", m_depthComputeBuffer);
            m_renderMaterial.SetBuffer("vertexColor", m_colorComputeBuffer);
            m_renderMaterial.SetBuffer("distanceToOrigin", m_distanceComputeBuffer);

            m_isInitialised = true;
        }

        private void LateUpdate()
        {
            if (!m_isInitialised)
            {
                return;
            }

            // make sure to have enough images before processing
            uint numImages = 0;
            if (m_imageColorSub.isReady())
            {
                m_colorTexture = m_imageColorSub.GetLatestTexture2D();
                numImages++;
            }
            if (m_imageDepthSub.isReady())
            {
                m_depthTexture = m_imageDepthSub.GetLatestTexture2D();
                numImages++;
            }
            if (numImages == 2)
            {
                GPUGeneratePointCloud();
            }
        }

        private void GPUGeneratePointCloud()
        {
            // Set inputs for GPU kernels
            ComputeShader.SetTexture(m_kernelHandleDepth, "inColor", m_colorTexture);
            ComputeShader.SetTexture(m_kernelHandleDepth, "inDepth", m_depthTexture);

            // Set camera origin matrix
            ComputeShader.SetMatrix("originTransform", m_pointCloudOriginTransform.localToWorldMatrix);

            // Dispatch to invoke GPU computing
            ComputeShader.Dispatch(m_kernelHandleDepth, (int)m_colorImageWidth / 8, (int)m_colorImageHeight / 8, 1);

            Graphics.DrawProcedural(m_renderMaterial, m_defaultBounds, MeshTopology.Points, m_totalNumVertices, 1);
        }

        private void OnDestroy()
        {
            if (m_depthComputeBuffer != null)
            {
                m_depthComputeBuffer.Release();
                m_depthComputeBuffer = null;
            }

            if (m_colorComputeBuffer != null)
            {
                m_colorComputeBuffer.Release();
                m_colorComputeBuffer = null;
            }

            if (m_distanceComputeBuffer != null)
            {
                m_distanceComputeBuffer.Release();
                m_distanceComputeBuffer = null;
            }
        }
    }
}
