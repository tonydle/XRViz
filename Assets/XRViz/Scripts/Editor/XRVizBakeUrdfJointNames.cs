using UnityEngine;
using UnityEditor;
using Unity.Robotics;

// Copies each joint's URDF name off the URDF Importer's UrdfJoint component into a plain
// UrdfJointName component that lives in Assembly-CSharp.
//
// Needed because Unity.Robotics.UrdfImporter is a desktop-only assembly (its asmdef lists
// Editor/Win64/Linux64/macOS only, since it ships native AssimpNet and VHACD binaries with no
// Android build). Editor scripts like this one can still read it; a Quest APK cannot. Run this
// on a robot once and its joints keep tracking /joint_states in a standalone Android build.
public static class XRVizBakeUrdfJointNames
{
    [MenuItem("XRViz/Bake URDF Joint Names")]
    public static void Run()
    {
        var root = Selection.activeGameObject;
        if (root == null)
        {
            Debug.LogWarning("[XRViz] Select a URDF-imported robot root first.");
            return;
        }

        int baked = Bake(root);
        if (baked == 0)
        {
            Debug.LogWarning($"[XRViz] No UrdfJoint components found under '{root.name}'. Select the " +
                "root of a URDF-imported robot (open the prefab, or select the prefab asset).");
            return;
        }

        Debug.Log($"[XRViz] Baked {baked} URDF joint name(s) under '{root.name}'. Save the prefab/scene " +
            "to keep them - these are what /joint_states matches against in an Android build.");
    }

    // Returns how many joint names were written. Safe to re-run; it overwrites in place.
    public static int Bake(GameObject root)
    {
        int baked = 0;
        // GetComponent<UrdfJoint> picks up every subclass (revolute, continuous, prismatic,
        // fixed, ...), so fixed joints get a name too - harmless, and it keeps this independent
        // of which joint types a given robot happens to use
        foreach (var urdfJoint in root.GetComponentsInChildren<Unity.Robotics.UrdfImporter.UrdfJoint>(true))
        {
            if (string.IsNullOrEmpty(urdfJoint.jointName))
                continue;

            var target = urdfJoint.gameObject;
            var nameComponent = target.GetComponent<UrdfJointName>();
            if (nameComponent == null)
                nameComponent = Undo.AddComponent<UrdfJointName>(target);

            Undo.RecordObject(nameComponent, "Bake URDF Joint Name");
            var so = new SerializedObject(nameComponent);
            so.FindProperty("_jointName").stringValue = urdfJoint.jointName;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(nameComponent);
            baked++;
        }

        if (baked > 0)
            PrefabUtility.RecordPrefabInstancePropertyModifications(root.transform);

        return baked;
    }
}
