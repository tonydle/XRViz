namespace Unity.Robotics
{
    // Implemented by a subscriber whose messages carry a header frame_id, so a TfAnchor can learn
    // which TF frame its data is expressed in rather than having it typed in by hand.
    //
    // This is what makes TF anchoring survive the topic browser: retarget a subscriber from
    // /camera_a/depth to /camera_b/depth in the headset and the anchor follows the new frame the
    // moment the first message lands, with nothing to re-enter (there is no keyboard in there).
    public interface IRosFrameSource
    {
        // header.frame_id of the most recent message, or empty before one has arrived
        string FrameId { get; }
    }
}
