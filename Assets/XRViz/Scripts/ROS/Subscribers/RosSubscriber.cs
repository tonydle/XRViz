using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using System.Collections.Generic;

namespace Unity.Robotics
{
    public abstract class RosSubscriber<T> : MonoBehaviour, IRosSubscriberDiagnostics, IRosResubscribe
        where T: ROSTCPConnector.MessageGeneration.Message
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
            RosSubscriberRegistry.Add(this);
            Subscribe();
        }

        // Detach from the endpoint when this object goes away. A visualisation can be closed at
        // runtime now (the panel's Views tab destroys the copies it made), and a destroyed
        // subscriber whose callback is still registered means ROSConnection goes on deserializing
        // its topic and handing the messages to a dead object.
        //
        // Virtual, and declared here rather than left to Unity's message dispatch, because two
        // subclasses already define OnDestroy for their textures. Unity calls the most-derived
        // OnDestroy only, so a private one here would silently never run for those two - the same
        // trap that keeps LateUpdate out of this class.
        protected virtual void OnDestroy()
        {
            string topic = _topic;
            Unsubscribe();
            RosSubscriberRegistry.Remove(this);
            RosSubscriberRegistry.RestoreOthers(this, topic);
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

            string previous = _topic;
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

            // Unsubscribe() took every callback on the old topic with it, including any other
            // subscriber sharing it - put those back before anyone notices they stopped
            int restored = RosSubscriberRegistry.RestoreOthers(this, previous);
            if (restored > 0)
                Debug.Log($"[XRViz] '{name}' left {previous}; re-registered {restored} other " +
                    "subscriber(s) still bound to it.", this);

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
            // This drops every callback registered on the topic, not just ours. Callers are
            // responsible for putting the others back - see RosSubscriberRegistry.RestoreOthers,
            // which both paths out of here (SetTopic and OnDestroy) call.
            ROSConnection.GetOrCreateInstance().Unsubscribe(_topic);
            _subscribed = false;
        }

        // IRosResubscribe: re-register a callback that somebody else's unsubscribe removed. Not
        // a retarget - the topic has not changed, so nothing about the queue or the counters is
        // reset; as far as this subscriber is concerned nothing happened, which is the point.
        void IRosResubscribe.RestoreSubscription()
        {
            if (string.IsNullOrEmpty(_topic))
                return;
            ROSConnection.GetOrCreateInstance().Subscribe<T>(_topic, ReceiveCallback);
            _subscribed = true;
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
