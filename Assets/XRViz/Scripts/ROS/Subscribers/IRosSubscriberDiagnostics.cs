namespace Unity.Robotics
{
    // What a subscriber can say about itself when nothing is drawing and nobody knows why.
    //
    // Deliberately about the SUBSCRIBER rather than about an expected topic name: XRViz binds any
    // compatible topic to any subscriber at runtime, and nothing in it may depend on a topic
    // being called the conventional thing. So the question a diagnostic answers is "what is this
    // subscriber bound to, and what has reached it", never "is /scan publishing".
    //
    // The four states this separates are the four different fixes:
    //   no topic bound      - pick one on the Topics tab
    //   bound, 0 messages   - wrong topic, wrong type, or nothing is publishing
    //   messages, but stale - it published and stopped
    //   messages, flowing   - the problem is downstream, in the visualiser or its placement
    public interface IRosSubscriberDiagnostics : IRosTopicBinding
    {
        // Whether Subscribe() has actually run. False while the topic is empty.
        bool Subscribed { get; }

        // Messages handed to this subscriber since it bound to its current topic
        int MessagesReceived { get; }

        // Time.realtimeSinceStartup of the most recent one, or -1 if none has arrived. Arrival
        // time, not the header stamp - the header is the sensor's clock.
        float LastMessageRealtime { get; }
    }
}
