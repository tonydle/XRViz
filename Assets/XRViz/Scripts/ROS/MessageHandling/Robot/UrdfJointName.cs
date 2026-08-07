using UnityEngine;

namespace Unity.Robotics
{
    // Holds the URDF joint name for the ArticulationBody on this GameObject, baked in at edit
    // time by XRViz > Bake URDF Joint Names (also run as part of Add Joint State Components).
    //
    // This exists because the URDF Importer's runtime assembly (Unity.Robotics.UrdfImporter)
    // is desktop-only - its asmdef lists Editor/Win64/Linux64/macOS, since it ships native
    // AssimpNet and VHACD binaries with no Android build. So UrdfJoint.jointName is simply not
    // readable in a Quest APK, and anything that needs it at runtime has to have it baked into
    // a component that lives in Assembly-CSharp instead. See Docs/MVP_QUEST3_SETUP.md.
    public class UrdfJointName : MonoBehaviour
    {
        [SerializeField] private string _jointName;

        public string JointName => _jointName;

        public void SetJointName(string jointName)
        {
            _jointName = jointName;
        }
    }
}
