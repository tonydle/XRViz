using System.Collections.Generic;

namespace Unity.Robotics
{
    // What a live subscriber has to expose for its callback to be put back after somebody else
    // dropped the topic out from under it. Deliberately tiny and separate from IRosTopicBinding:
    // nothing outside the subscriber machinery should be re-registering callbacks.
    public interface IRosResubscribe
    {
        string Topic { get; }

        bool Subscribed { get; }

        // Re-register this subscriber's callback on its current topic. Safe to call when it is
        // already registered - ROSConnection keeps one callback list per topic and adding the
        // same delegate twice would only double-deliver, which Subscribe() guards against by
        // being called only from here and from SetTopic.
        void RestoreSubscription();
    }

    // Every subscriber that has run Start, so that one unsubscribing can repair the others.
    //
    // ROSConnection.Unsubscribe(topic) removes EVERY callback registered on that topic, not just
    // the caller's. That was harmless while one component owned one topic, which was true of the
    // generated scene until visualisations could be duplicated at runtime - two image windows
    // pointed at the same camera is now an ordinary thing to do, and without this the moment one
    // of them is retargeted or closed the other goes silent with nothing at all to show for it:
    // still "bound", still counting zero messages, indistinguishable from a topic that stopped
    // publishing.
    //
    // A plain static list rather than a MonoBehaviour singleton: it holds no state worth
    // inspecting, must exist before any Start runs, and a domain reload clears it along with the
    // subscribers it tracks. Entries are added in Start and removed in OnDestroy, so a subscriber
    // on an object that was never activated is simply not in here - it has no callback to lose.
    public static class RosSubscriberRegistry
    {
        private static readonly List<IRosResubscribe> s_Live = new List<IRosResubscribe>();

        public static void Add(IRosResubscribe subscriber)
        {
            if (subscriber != null && !s_Live.Contains(subscriber))
                s_Live.Add(subscriber);
        }

        public static void Remove(IRosResubscribe subscriber)
        {
            s_Live.Remove(subscriber);
        }

        // Put back the callbacks that unsubscribing from `topic` just removed. Call it straight
        // after ROSConnection.Unsubscribe, passing the subscriber that asked for it so it does
        // not resurrect its own.
        //
        // Returns how many were restored, which is the number worth logging when it is not zero:
        // "retargeting this one silently killed that one" is otherwise invisible.
        public static int RestoreOthers(IRosResubscribe leaving, string topic)
        {
            if (string.IsNullOrEmpty(topic))
                return 0;

            int restored = 0;
            for (int i = 0; i < s_Live.Count; i++)
            {
                var other = s_Live[i];
                if (other == null || ReferenceEquals(other, leaving))
                    continue;
                if (!other.Subscribed || other.Topic != topic)
                    continue;

                other.RestoreSubscription();
                restored++;
            }
            return restored;
        }

        // How many live subscribers are bound to a topic. The UI uses it to say that detaching
        // one of them leaves the others running, rather than implying the topic goes quiet.
        public static int CountOn(string topic)
        {
            if (string.IsNullOrEmpty(topic))
                return 0;

            int count = 0;
            for (int i = 0; i < s_Live.Count; i++)
            {
                if (s_Live[i] != null && s_Live[i].Subscribed && s_Live[i].Topic == topic)
                    count++;
            }
            return count;
        }
    }
}
