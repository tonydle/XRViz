using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    public class CompressedImageToRenderTexture : MonoBehaviour
    {
        [SerializeReference] private RenderTexture renderTexture;
        [SerializeReference] private RosSubscriberCompressedImage imageSub;
        [SerializeField] private float displayFrameRate = 30f;
        private Texture2D displayTexture2D;
        private float m_lastFrameUpdateTime = 0f;

        private void Start()
        {
            displayTexture2D = new Texture2D(1, 1);
        }

        private void Update()
        {
            if(imageSub.isReady())
            {
                displayTexture2D = imageSub.GetLatestTexture2D();

                if(Time.time - m_lastFrameUpdateTime > 1/displayFrameRate)
                {
                    m_lastFrameUpdateTime = Time.time;
                    Graphics.Blit(displayTexture2D, renderTexture);
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