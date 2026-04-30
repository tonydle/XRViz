using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Unity.Robotics
{
    public class BoolStatusUpdater : MonoBehaviour
    {
        [SerializeField] private RosSubscriberBool rosSubscriber;
        [SerializeField] private Image UIImage;
        [SerializeField] private TMP_Text UIText;
        [SerializeField] private Color falseColor = Color.red;
        [SerializeField] private Color trueColor = Color.green;
        [SerializeField] private string falseText = "False";
        [SerializeField] private string trueText = "True";

        private void Update()
        {
            if (rosSubscriber.isReady())
            {
                UpdateUI(rosSubscriber.getLatestValue());
            }
        }

        private void UpdateUI(bool status)
        {
            if (status)
            {
                UIImage.color = trueColor;
                UIText.color = trueColor;
                UIText.text = trueText;
            }
            else
            {
                UIImage.color = falseColor;
                UIText.color = falseColor;
                UIText.text = falseText;
            }
        }
    }
}
