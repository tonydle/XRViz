using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    public class ImageToMeshRenderer : MonoBehaviour
    {        
        [SerializeReference] private MeshRenderer meshRenderer;
        [SerializeReference] private RosSubscriberImage imageSub;
        [SerializeField] private float displayFrameRate = 30f;
        // [SerializeField] private Material material;
        private Texture2D displayTexture2D;
        private float m_lastFrameUpdateTime = 0f;

        private void Start()
        {
            displayTexture2D = new Texture2D(1, 1);
            // if(meshRenderer != null)
            // {
            //     meshRenderer.material = material;
            // }
        }

        private void Update()
        {
            if(imageSub.isReady())
            {
                displayTexture2D = imageSub.GetLatestTexture2D();

                if(Time.time - m_lastFrameUpdateTime > 1/displayFrameRate)
                {
                    m_lastFrameUpdateTime = Time.time;
                    // apply image as 2D Texture of MeshRenderer's default material
                    Vector3 meshScale = meshRenderer.transform.localScale;
                    meshScale.x = ((float)displayTexture2D.width/(float)displayTexture2D.height) * meshScale.z;
                    meshRenderer.transform.localScale = meshScale;
                    meshRenderer.material.mainTexture = displayTexture2D;
                }
            }
        }

        public void SetDisplayFrameRate(float framerate)
        {
            displayFrameRate = framerate;
        }

        public void SetDisplayFrameRate(Text framerate)
        {
            displayFrameRate = float.Parse(framerate.text);
        }
    }
}