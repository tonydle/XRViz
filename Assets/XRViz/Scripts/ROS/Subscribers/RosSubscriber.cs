using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using System.Collections.Generic;

namespace Unity.Robotics
{
    public abstract class RosSubscriber<T> : MonoBehaviour, IRosTopicBinding where T: ROSTCPConnector.MessageGeneration.Message
    {
        [SerializeField] protected string _topic = "";
        [SerializeField] protected int _queueSize = 1;
        protected Queue<T> _incomingMessages;
        T _latestMessage;
        protected bool _newMessageAvailable = false;
        bool _subscribed = false;

        public string Topic => _topic;

        public string RosMessageName => MessageRegistry.GetRosMessageName<T>();

        protected virtual void Start()
        {
            // create a Queue of _queueSize initial capacity (which can increases)
            _incomingMessages = new Queue<T>(_queueSize);
            Subscribe();
        }

        protected virtual void Update()
        {
            // if no new messages
            if(_incomingMessages.Count == 0)
                return;

            //flush old messages
            while(_incomingMessages.Count > _queueSize)
            {
                _incomingMessages.Dequeue();
            }
        }

        // Before Start this only stores the topic, so it still works as plain configuration
        // (that's how the editor tooling and the robot prefabs use it). Once subscribed it
        // rebinds live, which is what lets the topic be picked from the headset at runtime.
        public void SetTopic(string topic)
        {
            if (_topic == topic)
                return;

            if (!_subscribed)
            {
                _topic = topic;
                return;
            }

            Unsubscribe();
            _topic = topic;

            // Drop anything still queued from the old topic - it isn't ours anymore
            _incomingMessages.Clear();
            _newMessageAvailable = false;

            Subscribe();
            OnTopicChanged();
        }

        // Hook for subclasses to drop state derived from the previous topic's messages
        protected virtual void OnTopicChanged()
        {
        }

        void Subscribe()
        {
            if (string.IsNullOrEmpty(_topic))
                return;
            ROSConnection.GetOrCreateInstance().Subscribe<T>(_topic, ReceiveCallback);
            _subscribed = true;
        }

        void Unsubscribe()
        {
            if (!_subscribed || string.IsNullOrEmpty(_topic))
                return;
            // Note this drops every callback registered on the topic, not just ours - fine while
            // one component owns a topic, which is the case throughout XRViz
            ROSConnection.GetOrCreateInstance().Unsubscribe(_topic);
            _subscribed = false;
        }

        protected bool NewMessageAvailable()
        {
            return _newMessageAvailable;
        }

        protected void ReceiveCallback(T message)
        {
            if(message != null)
            {
                _latestMessage = message;
                _incomingMessages.Enqueue(_latestMessage);
                _newMessageAvailable = true;
            }
        }

        protected T GetLatestMessage()
        {
            _newMessageAvailable = false;
            return _latestMessage;
        }

        protected Queue<T> GetMessageQueue()
        {
            _newMessageAvailable = false;
            return _incomingMessages;
        }
    }
}
