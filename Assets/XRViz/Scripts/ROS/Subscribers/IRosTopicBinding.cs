namespace Unity.Robotics
{
    // Lets UI retarget a subscriber at a different ROS topic at runtime without knowing its
    // message type. Implemented by RosSubscriber<T>; TopicBrowserUI drives it from the headset.
    public interface IRosTopicBinding
    {
        // The topic currently bound, e.g. "/joint_states"
        string Topic { get; }

        // The ROS message type this binding can accept, e.g. "sensor_msgs/JointState".
        // Used to filter the topic list down to topics that are actually compatible.
        string RosMessageName { get; }

        // Before Start this just configures the topic; afterwards it unsubscribes from the old
        // topic and subscribes to the new one.
        void SetTopic(string topic);
    }
}
