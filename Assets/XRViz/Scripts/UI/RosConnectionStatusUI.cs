using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Unity.Robotics
{
    public class RosConnectionStatusUI : MonoBehaviour
    {
        [SerializeField] private RosSubscriberJointState _jointStateSub;
        [SerializeField] private TMP_Text _statusText;
        [SerializeField] private float _refreshInterval = 0.25f;

        private ROSConnection _ros;
        private float _nextRefreshTime;
        private int _lastStampSec;
        private uint _lastStampNanosec;
        private float _lastJointStateRealtime = -1f;

        private void Start()
        {
            _ros = ROSConnection.GetOrCreateInstance();
        }

        public void Connect()
        {
            _ros?.Connect();
        }

        public void Disconnect()
        {
            _ros?.Disconnect();
        }

        public void SetIpAddress(string ip)
        {
            if (_ros == null)
                return;
            _ros.Disconnect();
            _ros.RosIPAddress = ip;
        }

        private void Update()
        {
            TrackJointStateArrival();

            if (_ros == null || _statusText == null || Time.realtimeSinceStartup < _nextRefreshTime)
                return;
            _nextRefreshTime = Time.realtimeSinceStartup + _refreshInterval;

            string connection;
            if (!_ros.HasConnectionThread)
                connection = "<color=#FFB300>connecting…</color>";
            else if (_ros.HasConnectionError)
                connection = "<color=#FF5252>error</color>";
            else
                connection = "<color=#4CAF50>ok</color>";

            // LastMessageReceivedRealtime stays 0 until the first message arrives
            float lastReceived = _ros.LastMessageReceivedRealtime;
            string lastReceivedText = lastReceived <= 0f
                ? "none yet"
                : $"{Time.realtimeSinceStartup - lastReceived:0.0} s ago";

            string jointStateText = _lastJointStateRealtime < 0f
                ? "none yet"
                : $"{Time.realtimeSinceStartup - _lastJointStateRealtime:0.0} s ago";

            _statusText.text =
                $"ROS {_ros.RosIPAddress}:{_ros.RosPort}\n" +
                $"Connection: {connection}\n" +
                $"Last message: {lastReceivedText}\n" +
                $"Joint states: {jointStateText}";
        }

        // A new joint state is detected by a change in the message header stamp,
        // so the existing subscriber does not need modification.
        private void TrackJointStateArrival()
        {
            if (_jointStateSub == null)
                return;

            var stamp = _jointStateSub.GetLatestTime();
            if (stamp == null)
                return;

            if (stamp.sec != _lastStampSec || stamp.nanosec != _lastStampNanosec)
            {
                _lastStampSec = stamp.sec;
                _lastStampNanosec = stamp.nanosec;
                _lastJointStateRealtime = Time.realtimeSinceStartup;
            }
        }
    }
}
