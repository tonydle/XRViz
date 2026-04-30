using System.Collections;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosTriggerRequest = RosMessageTypes.Std.TriggerRequest;
using RosTriggerResponse = RosMessageTypes.Std.TriggerResponse;

namespace Unity.Robotics
{
    public class RosServiceTriggerClient : MonoBehaviour
    {
        [SerializeField] private string _serviceName = "";
        [SerializeField] private float _serviceTimeOut = 5.0f;
        private bool _serviceDone = false;
        private bool _timedOut = false;
        private RosTriggerRequest _request;
        private RosTriggerResponse _response;
        private float serviceStartTime;

        private void Start()
        {
            _request = new RosTriggerRequest();
            _response = new RosTriggerResponse();
            ROSConnection.GetOrCreateInstance().RegisterRosService<RosTriggerRequest,RosTriggerResponse>(_serviceName);
        }

        private void Update()
        {
            if(Time.time - serviceStartTime > _serviceTimeOut)
            {
                if(!_serviceDone)
                {
                    _serviceDone = true;
                    _timedOut = true;
                }
            }
        }

        public void SendTrigger()
        {
            _serviceDone = false;
            _timedOut = false;
            serviceStartTime = Time.time;
            ROSConnection.GetOrCreateInstance().SendServiceMessage<RosTriggerResponse>(_serviceName, _request, TriggerResponseCallback);
            Debug.Log($"Sent trigger request to service: {_serviceName}");
        }

        public bool ServiceDone()
        {
            return _serviceDone;
        }

        public bool TimedOut()
        {
            return _timedOut;
        }

        public RosTriggerResponse GetResponse()
        {
            return _response;
        }

        public bool GetSuccess()
        {
            return _response.success;
        }

        public string GetMessage()
        {
            return _response.message;
        }

        private void TriggerResponseCallback(RosTriggerResponse response)
        {
            _response = response;
            _serviceDone = true;
            _timedOut = false;
            Debug.Log($"Received response from service: {_serviceName}, Success: {_response.success}, Message: {_response.message}");
        }
    }
}