using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using System.Collections.Generic;

namespace Unity.Robotics
{
    public abstract class RosSubscriber<T> : MonoBehaviour, IRosSubscriberDiagnostics where T: ROSTCPConnector.MessageGeneration.Message
    {
        [SerializeField] protected string _topic = "";
        [SerializeField] protected int _queueSize = 1;
        protected Queue<T> _incomingMessages;
        T _latestMessage;
        protected bool _newMessageAvailable = false;
        bool _subscribed = false;

        // Whether Start has run. NOT the same question as "are we subscribed", which is what
        // this used to be confused with - see SetTopic.
        bool _started = false;

        // Counted on whatever thread the callback arrives on; the timestamp is taken on the
        // main thread in LateUpdate, because Time.realtimeSinceStartup throws off it. LateUpdate
        // rather than Update because every subclass overrides Update, and one that forgot to call
        // base.Update() would silently stop reporting.
        private int _receivedCount;
        private int _timestampedCount;
        private float _lastMessageRealtime = -1f;

        public string Topic => _topic;

        public bool Subscribed => _subscribed;

        public int MessagesReceived => _receivedCount;

        public float LastMessageRealtime => _lastMessageRealtime;

        public string RosMessageName => MessageRegistry.GetRosMessageName<T>();

        protected virtual void Start()
        {
            // create a Queue of _queueSize initial capacity (which can increases)
            _incomingMessages = new Queue<T>(_queueSize);
            _started = true;
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
        // (that's how the editor tooling and the robot prefabs use it). Afterwards it rebinds
        // live, which is what lets the topic be picked from the headset at runtime.
        //
        // The guard is on _started, NOT on _subscribed. It used to be on _subscribed, and that
        // was wrong in exactly the case the whole scene is built around: every visualisation in
        // the generated scene starts with an EMPTY topic, Subscribe() returns early on an empty
        // topic without setting _subscribed, so _subscribed stayed false forever - and the first
        // pick from the Topics browser took this branch, stored the name, and never subscribed.
        // The topic then showed as bound everywhere in the UI (Topic returns it, the row goes
        // green) while not one message could ever arrive. Only the robot escaped it, because its
        // prefab ships with /joint_states already set, so it did subscribe at Start.
        public void SetTopic(string topic)
        {
            if (_topic == topic)
                return;

            if (!_started)
            {
                _topic = topic;
                return;
            }

            Unsubscribe();
            _topic = topic;

            // Drop anything still queued from the old topic - it isn't ours anymore
            _incomingMessages.Clear();
            _newMessageAvailable = false;

            // Counts are per-topic, so "0 messages" after a retarget means this topic is silent
            // rather than that the last one was
            _receivedCount = 0;
            _timestampedCount = 0;
            _lastMessageRealtime = -1f;

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
                System.Threading.Interlocked.Increment(ref _receivedCount);
            }
        }

        // Main thread: turn "something arrived" into "something arrived at this time"
        private void LateUpdate()
        {
            int received = _receivedCount;
            if (received == _timestampedCount)
                return;
            _timestampedCount = received;
            _lastMessageRealtime = Time.realtimeSinceStartup;
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
