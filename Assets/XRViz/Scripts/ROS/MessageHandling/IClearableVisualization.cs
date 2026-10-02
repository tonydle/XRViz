namespace Unity.Robotics
{
    // A visualisation that holds onto geometry between messages and can be told to drop it.
    //
    // Anything that draws into a mesh or a compute buffer keeps showing its last frame after the
    // topic goes quiet, and a frozen frame is indistinguishable from a live one - which is worse
    // than showing nothing, because you act on it. Implement this and the ROS control panel's
    // "Clear Data" button picks the visualisation up automatically; it finds implementors rather
    // than holding a serialized list.
    //
    // Clear() must also drop whatever the subscriber has parsed, not just the drawn geometry:
    // most visualisers rebuild every frame from the subscriber's last message, so clearing only
    // the geometry lets the next frame put it straight back.
    public interface IClearableVisualization
    {
        void Clear();
    }
}
