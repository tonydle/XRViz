using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Unity.Robotics
{
    public class RosConnectionStatusUI : MonoBehaviour
    {
        [SerializeField] private RosSubscriberJointState _jointStateSub;
        [SerializeField] private TMP_Text _statusText;

        // The same connection state in two words, drawn in the panel's header so it is on
        // screen whichever page is open. The detailed block below lives on the ROS page only,
        // and "is this thing even connected?" is the question you have while looking at the
        // other five.
        [SerializeField] private TMP_Text _headerStatus;

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
            string header;
            if (!_ros.HasConnectionThread)
            {
                connection = "<color=#FFB300>connecting…</color>";
                header = "<color=#FFB300>• connecting</color>";
            }
            else if (_ros.HasConnectionError)
            {
                connection = "<color=#FF5252>error</color>";
                header = "<color=#FF5252>• error</color>";
            }
            else
            {
                connection = "<color=#4CAF50>ok</color>";
                header = "<color=#4CAF50>• connected</color>";
            }

            // Always on screen, whichever page is open
            if (_headerStatus != null)
                _headerStatus.text = header;

            // LastMessageReceivedRealtime stays 0 until the first message arrives
            float lastReceived = _ros.LastMessageReceivedRealtime;
            string lastReceivedText = lastReceived <= 0f
                ? "none yet"
                : $"{Time.realtimeSinceStartup - lastReceived:0.0} s ago";

            string jointStateText = _lastJointStateRealtime < 0f
                ? "none yet"
                : $"{Time.realtimeSinceStartup - _lastJointStateRealtime:0.0} s ago";

            // Label then value, one per line, labels muted: at arm's length through passthrough
            // the eye finds a value by its label, not by counting words into a sentence
            _statusText.text =
                $"<color=#9AA5B1>Endpoint</color>  {_ros.RosIPAddress}:{_ros.RosPort}\n" +
                $"<color=#9AA5B1>Connection</color>  {connection}\n" +
                $"<color=#9AA5B1>Last message</color>  {lastReceivedText}\n" +
                $"<color=#9AA5B1>Joint states</color>  {jointStateText}";
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
