using UnityEngine;
using UnityEditor;
using System.Xml;
using Unity.Robotics;

public static class XRVizAddJointStateComponents
{
    [MenuItem("XRViz/Add Joint State Components")]
    public static void Run()
    {
        var root = Selection.activeGameObject;
        if (root == null)
        {
            Debug.LogWarning("Select a top‐level GameObject first.");
            return;
        }

        Undo.RegisterCompleteObjectUndo(root, "Add Joint State Components");

        // 1) RosSubscriberJointState
        var subscriber = root.GetComponent<RosSubscriberJointState>();
        if (subscriber == null)
        {
            subscriber = Undo.AddComponent<RosSubscriberJointState>(root);
            subscriber.SetTopic("/joint_states");
        }
            
        Debug.Log($"[XRViz] RosSubscriberJointState on {GetPath(root)}");

        // 2) RobotStateWriterController
        var writer = root.GetComponent<RobotStateWriterController>();
        if (writer == null)
            writer = Undo.AddComponent<RobotStateWriterController>(root);
        Debug.Log($"[XRViz] RobotStateWriterController on {GetPath(root)}");

        // 3) URDF selection and mimic joint setup
        string urdfPath = EditorUtility.OpenFilePanel("Select URDF file", Application.dataPath, "urdf");
        if (!string.IsNullOrEmpty(urdfPath))
            PopulateMimicJoints(writer, urdfPath);
        else
            Debug.LogWarning("[XRViz] URDF not selected; mimic joints not configured.");

        // 4) RobotStateWriterControllerRos
        var rosWriter = root.GetComponent<RobotStateWriterControllerRos>();
        if (rosWriter == null)
            rosWriter = Undo.AddComponent<RobotStateWriterControllerRos>(root);

        // 5) Link components
        Undo.RecordObject(rosWriter, "Link Joint State Components");
        var so = new SerializedObject(rosWriter);
        so.FindProperty("stateWriterController").objectReferenceValue = writer;
        so.FindProperty("jointStateSub").objectReferenceValue = subscriber;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(rosWriter);

        Debug.Log($"[XRViz] RobotStateWriterControllerRos linked on {GetPath(root)}");
        Debug.Log("[XRViz] Add Joint State Components complete.");
    }

    static void PopulateMimicJoints(RobotStateWriterController writer, string urdfPath)
    {
        var so = new SerializedObject(writer);
        var sp = so.FindProperty("mimicJoints");
        sp.ClearArray();

        var xml = new XmlDocument();
        xml.Load(urdfPath);
        XmlNodeList jointsWithMimic = xml.SelectNodes("//joint[mimic]");

        for (int i = 0; i < jointsWithMimic.Count; i++)
        {
            var jointNode = jointsWithMimic[i];
            string target = jointNode.Attributes["name"]?.Value;
            var mimicNode = jointNode.SelectSingleNode("mimic");
            if (mimicNode == null) continue;

            string source = mimicNode.Attributes["joint"]?.Value;
            float multiplier = 1f;
            float offset = 0f;
            if (mimicNode.Attributes["multiplier"] != null)
                float.TryParse(mimicNode.Attributes["multiplier"].Value, out multiplier);
            if (mimicNode.Attributes["offset"] != null)
                float.TryParse(mimicNode.Attributes["offset"].Value, out offset);

            sp.InsertArrayElementAtIndex(i);
            var elem = sp.GetArrayElementAtIndex(i);
            elem.FindPropertyRelative("targetJointName").stringValue = target;
            elem.FindPropertyRelative("mimicJointName").stringValue = source;
            elem.FindPropertyRelative("multiplier").floatValue = multiplier;
            elem.FindPropertyRelative("offset").floatValue = offset;
            Debug.Log($"[XRViz] Configured mimic: {target} <- {source} * {multiplier} + {offset}");
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(writer);
    }

    static string GetPath(GameObject go)
    {
        return go.transform.parent == null
            ? go.name
            : GetPath(go.transform.parent.gameObject) + "/" + go.name;
    }
}