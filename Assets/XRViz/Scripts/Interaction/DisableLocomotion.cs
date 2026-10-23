using Oculus.Interaction.Locomotion;
using UnityEngine;

namespace Unity.Robotics
{
    // Switches off the thumbstick locomotion (teleport, snap/smooth turn, slide, step) that comes
    // with the Comprehensive Interaction Rig Building Block.
    //
    // Locomotion moves the camera rig, and in passthrough MR that is the same as moving the whole
    // virtual world against the real room: one nudge of the stick and the calibrated robot, the
    // TF origin and every cloud slide off the real things they were lined up with. This app is
    // used standing in the room next to the robot - you walk, you don't fly - so none of it is
    // wanted. It also frees the right thumbstick for the calibration's height trim, which it
    // used to share with the turner.
    //
    // A component rather than an edit to the scene, because the rig is a hand-added Building
    // Block that the scene generator cannot reach - and an override buried in a nested prefab is
    // silently lost the next time the block is re-added.
    //
    // Components are disabled rather than their GameObjects deactivated, so nothing else that
    // happens to share an object goes with them. Everything in the chain is switched off, not
    // just the sink: FirstPersonLocomotor applies some events the moment they arrive, and a
    // disabled MonoBehaviour can still have methods called on it. With the broadcasters and the
    // connections off as well, no event is ever raised to deliver. The interactors go too, so
    // pushing the stick doesn't draw a teleport arc that then does nothing.
    public class DisableLocomotion : MonoBehaviour
    {
        // Start, not Awake: every rig object has been loaded by then, and disabling a component
        // before its own Start has run simply means its Start never runs, which is fine here
        private void Start()
        {
            int disabled = 0;
            var behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var behaviour in behaviours)
            {
                if (!IsLocomotion(behaviour) || !behaviour.enabled)
                    continue;

                behaviour.enabled = false;
                disabled++;
            }

            Debug.Log($"[XRViz] Locomotion off: disabled {disabled} component(s), so the thumbsticks " +
                "no longer move the world.", this);
        }

        private static bool IsLocomotion(MonoBehaviour behaviour)
        {
            return behaviour is FirstPersonLocomotor
                || behaviour is PlayerLocomotor
                || behaviour is ILocomotionEventBroadcaster // teleport, turn, slide, step, and the connections between them
                || behaviour is LocomotionAxisTurnerInteractor
                || behaviour is LocomotionTurnerInteractor;
        }
    }
}
