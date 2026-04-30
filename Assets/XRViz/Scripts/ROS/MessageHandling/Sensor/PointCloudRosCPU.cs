using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    public class PointCloudRosCPU : MonoBehaviour
    {
        // To be linked in Unity Editor
        [SerializeField] private RosSubscriberCameraInfo m_camInfoColorSub;
        [SerializeField] private RosSubscriberCameraInfo m_camInfoDepthSub;
        [SerializeField] private RosSubscriberCompressedImage m_imageColorSub;
        [SerializeField] private RosSubscriberCompressedImage m_imageDepthLowerSub;
        [SerializeField] private RosSubscriberCompressedImage m_imageDepthUpperSub;
        [SerializeField] private MeshFilter m_meshFilter;
        [SerializeField] private float targetProcessingFramerate = 60f;
        [SerializeField] private int xColorOffset = 0;
        [SerializeField] private int yColorOffset = 0;
        [SerializeField] private int rowColSkipping = 0;
        // To be generated
        private Mesh m_PCMesh;
        private Stack<Vector3> m_PCPoints;
        private Stack<int> m_PCMeshIndecies;
        private Stack<Color> m_PCMeshColours;

        // To be subscribed from ROS
        private float[] m_camInfoColor;
        private float[] m_camInfoDepth;
        private uint m_colorImageWidth, m_colorImageHeight;
        private uint m_depthImageWidth, m_depthImageHeight;
        private bool m_cameraInfoAcquired = false;
        private Texture2D m_colorTexture;
        private Texture2D m_depthLowerTexture;
        private Texture2D m_depthUpperTexture;
        private bool m_pointCloudGenerating = false;
        private bool m_pointCloudGenerated = false;

        public void SetRowColSkipping(int skipping)
        {
            rowColSkipping = skipping;
        }

        public void SetRowColSkipping(Text skipping)
        {
            rowColSkipping = int.Parse(skipping.text);
        }

        void Start()
        {
            m_PCMesh = new Mesh();
            m_PCMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m_meshFilter.mesh = m_PCMesh;
        }

        private void Update()
        {
            if(!m_cameraInfoAcquired)
            {
                if(m_camInfoColorSub.isReady() && m_camInfoDepthSub.isReady())
                {
                    m_camInfoColor = m_camInfoColorSub.GetCameraNecessaryInfo();
                    m_camInfoDepth = m_camInfoDepthSub.GetCameraNecessaryInfo();
                    m_colorImageWidth = m_camInfoColorSub.GetImageWidth();
                    m_colorImageHeight = m_camInfoColorSub.GetImageHeight();
                    m_depthImageWidth = m_camInfoDepthSub.GetImageWidth();
                    m_depthImageHeight = m_camInfoDepthSub.GetImageHeight();
                    m_cameraInfoAcquired = true;
                }
            }
            else
            {
                // make sure to have enough images before processing
                uint numImages = 0;
                if(m_imageColorSub.isReady())
                {
                    m_colorTexture = m_imageColorSub.GetLatestTexture2D();
                    numImages++;
                }
                if(m_imageDepthLowerSub.isReady())
                {
                    m_depthLowerTexture = m_imageDepthLowerSub.GetLatestTexture2D();
                    numImages++;
                }
                if(m_imageDepthUpperSub.isReady())
                {
                    m_depthUpperTexture = m_imageDepthUpperSub.GetLatestTexture2D();
                    numImages++;
                }
                if(numImages == 3)
                {
                    if(m_pointCloudGenerated)
                        UpdateMesh();
                    if(!m_pointCloudGenerating)
                        StartCoroutine("FastGeneratePointCloud");
                }
            }
        }

        public void UpdateMesh()
        {
            m_PCMesh.Clear();
            m_PCMesh.vertices = m_PCPoints.ToArray();
            m_PCMesh.colors = m_PCMeshColours.ToArray();
            m_PCMesh.SetIndices(m_PCMeshIndecies.ToArray(), MeshTopology.Points, 0);
            m_PCMesh.uv = new Vector2[m_PCPoints.Count];
            m_PCMesh.normals = new Vector3[m_PCPoints.Count];
        }

        IEnumerator FastGeneratePointCloud()
        {
            m_pointCloudGenerating = true;
            m_pointCloudGenerated = false;
            float generationBeginTime = Time.realtimeSinceStartup;
            float lastProcessingTime = Time.realtimeSinceStartup;

            Texture2D colorTexture = m_colorTexture;
            Texture2D depthLowerTexture = m_depthLowerTexture;
            Texture2D depthUpperTexture = m_depthUpperTexture;

            Color32[] colorColor = colorTexture.GetPixels32();
            Color32[] colorDepthLower = depthLowerTexture.GetPixels32();
            Color32[] colorDepthUpper = depthUpperTexture.GetPixels32();

            m_PCPoints = new Stack<Vector3>();
            m_PCMeshIndecies = new Stack<int>();
            m_PCMeshColours = new Stack<Color>();

            int depthHeight = depthLowerTexture.height;
            int depthWidth = depthLowerTexture.width;
            int colorHeight = colorTexture.height;
            int colorWidth = colorTexture.width;

            int row = 0, col = 0;
            for(row = 0; row < depthHeight; row = row + rowColSkipping + 1)
            {
                for(col = 0; col < depthWidth; col = col + rowColSkipping + 1)
                {
                    if(Time.realtimeSinceStartup - lastProcessingTime < 1/targetProcessingFramerate)
                    {
                        // Using the green (g) channel of Color, because all r, g, b give the same value (grey)
                        float pixelDepth = ((float)colorDepthLower[row*depthWidth + col].g + (float)colorDepthUpper[row*depthWidth + col].g*256f)*0.001f;

                        if(pixelDepth >  0)
                        {
                            // Coordinate
                            // NEEDS TO BE LEFT HAND RULES
                            float x = (float)((col - m_camInfoDepth[0]) / m_camInfoDepth[2] * pixelDepth); 
                            float y = (float)((row - m_camInfoDepth[1]) / m_camInfoDepth[3] * pixelDepth); 
                            float z = (float)pixelDepth;
                            m_PCPoints.Push(new Vector3 (x, y, z));
                            m_PCMeshIndecies.Push(m_PCPoints.Count - 1);

                            // Colour
                            int colColor = (int)((x / z * m_camInfoColor[2]) + m_camInfoColor[0]) + xColorOffset;
                            int rowColor = (int)((y / z * m_camInfoColor[3]) + m_camInfoColor[1]) + yColorOffset;

                            Color color = Color.clear;
                            if(colColor >= 0 && colColor < colorWidth && rowColor >= 0 && rowColor < colorHeight)
                            {
                                color = colorColor[rowColor*colorWidth + colColor];
                            }
                            m_PCMeshColours.Push(color);
                        }
                    }
                    else
                    {
                        yield return null;
                        lastProcessingTime = Time.realtimeSinceStartup;
                    }
                }
            }

            // can print this out to debug efficiency
            float processTime = Time.realtimeSinceStartup - generationBeginTime;
            m_pointCloudGenerating = false;
            m_pointCloudGenerated = true;
            yield return null;
        }
    }
}