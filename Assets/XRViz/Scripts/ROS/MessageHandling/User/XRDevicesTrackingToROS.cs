using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Unity.Robotics
{
    public class XRDevicesTrackingToROS : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float _publishRate = 30.0f;
        [SerializeField] private Transform _referenceFrame;
        [SerializeField] private Camera _XRCamera;
        [SerializeField] private string _headTopicNamePrefix = "/xr/head";
        [SerializeField] private List<XRControllerData> _XRControllers;

        private RosPublisherPoseStamped _headPosePublisher;
        private List<(RosPublisherPoseStamped posePublisher, RosPublisherJoy joyPublisher)> _controllerPublishers;

        private float _lastPublishTime;
        
        [System.Serializable]
        public class XRControllerData
        {
            public enum Handedness { Left, Right }
            public Component controller;
            public Handedness hand;
            public string topicNamePrefix = "/xr/controller";
            [HideInInspector] public float[] axes { get; set; } = new float[2];
            [HideInInspector] public int[] buttons { get; set; } = new int[2];
            [HideInInspector] public float[] prevAxes { get; set; } = new float[2];
            [HideInInspector] public int[] prevButtons { get; set; } = new int[2];
        }

        private void Start()
        {
            _headPosePublisher = gameObject.AddComponent<RosPublisherPoseStamped>();
            _headPosePublisher.SetTopic(_headTopicNamePrefix + "/pose");

            _controllerPublishers = new List<(RosPublisherPoseStamped, RosPublisherJoy)>(_XRControllers.Count);
            foreach (var xrControllerData in _XRControllers)
            {
                var posePublisher = gameObject.AddComponent<RosPublisherPoseStamped>();
                posePublisher.SetTopic(xrControllerData.topicNamePrefix + "/pose");

                var joyPublisher = gameObject.AddComponent<RosPublisherJoy>();
                joyPublisher.SetTopic(xrControllerData.topicNamePrefix + "/joy");

                _controllerPublishers.Add((posePublisher, joyPublisher));
            }

            _lastPublishTime = Time.time;
        }

        private void Update()
        {
            if (Time.time - _lastPublishTime >= 1.0f / _publishRate)
            {
                _lastPublishTime = Time.time;
                PublishXRData();
            }
        }

        private void PublishXRData()
        {
            InputDevices.GetDeviceAtXRNode(XRNode.Head).TryGetFeatureValue(CommonUsages.userPresence, out bool headPresent);
            if (headPresent)
            {
                _headPosePublisher.Publish(_referenceFrame, _XRCamera.transform);
            }

            for (int i = 0; i < _XRControllers.Count; i++)
            {
                var controllerData = _XRControllers[i];
                
                XRNode handNode = (controllerData.hand == XRControllerData.Handedness.Left) ? XRNode.LeftHand : XRNode.RightHand;
                var inputDevice = InputDevices.GetDeviceAtXRNode(handNode);
                inputDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool controllerPresent);
                if (controllerPresent)
                {
                    var controller = controllerData.controller;
                    if (controller == null)
                    {
                        continue;
                    }

                    var posePublisher = _controllerPublishers[i].posePublisher;
                    var joyPublisher = _controllerPublishers[i].joyPublisher;

                    posePublisher.Publish(_referenceFrame, controller.transform);

                    inputDevice.TryGetFeatureValue(CommonUsages.grip, out float selectValue);
                    inputDevice.TryGetFeatureValue(CommonUsages.trigger, out float activateValue);
                    inputDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool selectActive);
                    inputDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool activateActive);
                    _XRControllers[i].axes[0] = selectValue;
                    _XRControllers[i].axes[1] = activateValue;
                    _XRControllers[i].buttons[0] = selectActive ? 1 : 0;
                    _XRControllers[i].buttons[1] = activateActive ? 1 : 0;

                    bool inputChanged = false;
                    for (int j = 0; j < _XRControllers[i].axes.Length; j++)
                    {
                        if (Mathf.Abs(_XRControllers[i].axes[j] - _XRControllers[i].prevAxes[j]) > 0.001)
                        {
                            _XRControllers[i].prevAxes[j] = _XRControllers[i].axes[j];
                            inputChanged = true;
                        }
                        if(_XRControllers[i].axes[j] < 0.001) _XRControllers[i].axes[j] = 0.0f;
                        if((1.0f - _XRControllers[i].axes[j]) < 0.001) _XRControllers[i].axes[j] = 1.0f;
                    }
                    for (int j= 0; j < _XRControllers[i].buttons.Length; j++)
                    {
                        if (_XRControllers[i].buttons[j] != _XRControllers[i].prevButtons[j])
                        {
                            _XRControllers[i].prevButtons[j] = _XRControllers[i].buttons[j];
                            inputChanged = true;
                        }
                    }
                    if (inputChanged)
                    {
                        joyPublisher.Publish(_XRControllers[i].topicNamePrefix, _XRControllers[i].axes, _XRControllers[i].buttons);
                    }
                }
            }
        }
    }
}
