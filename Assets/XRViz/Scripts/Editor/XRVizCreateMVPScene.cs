using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Robotics;

public static class XRVizCreateMVPScene
{
    const string k_ScenePath = "Assets/XRViz/Scenes/MVP_UR3e_MR.unity";
    const string k_RobotPrefabGuid = "5e41957997504bf458dbb54a5403d950"; // ur3e_rg2.prefab
    static readonly Vector3 k_RobotBasePosition = new Vector3(0f, 0.8f, 0.9f);

    [MenuItem("XRViz/Create MR MVP Scene (UR3e)")]
    public static void Run()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Passthrough shows the real room, but the virtual robot still needs a light
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // UR3e — the prefab already carries RosSubscriberJointState (/joint_states),
        // RobotStateWriterController (incl. RG2 mimic joints) and RobotStateWriterControllerRos
        string prefabPath = AssetDatabase.GUIDToAssetPath(k_RobotPrefabGuid);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[XRViz] ur3e_rg2 prefab not found (guid {k_RobotPrefabGuid}).");
            return;
        }
        var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        robot.transform.SetPositionAndRotation(k_RobotBasePosition, Quaternion.identity);
        var articulationBodies = robot.GetComponentsInChildren<ArticulationBody>();
        var robotRoot = articulationBodies.FirstOrDefault(b => b.isRoot);
        if (robotRoot == null)
            robotRoot = articulationBodies.FirstOrDefault();

        // Grab handle below the robot base; make it grabbable with the
        // Interaction SDK (see Docs/MVP_QUEST3_SETUP.md) and the robot follows
        var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        handle.name = "Robot Placement Handle";
        handle.transform.localScale = Vector3.one * 0.08f;
        handle.transform.position = k_RobotBasePosition + new Vector3(0f, -0.1f, 0f);
        var follower = handle.AddComponent<RobotPlacementFollower>();
        var followerSo = new SerializedObject(follower);
        followerSo.FindProperty("_robotRoot").objectReferenceValue = robotRoot;
        followerSo.FindProperty("_robotOffset").vector3Value = new Vector3(0f, 0.1f, 0f);
        followerSo.ApplyModifiedPropertiesWithoutUndo();

        // World-space ROS status panel
        var canvasGo = new GameObject("ROS Status Panel", typeof(Canvas));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(400f, 240f);
        canvasGo.transform.localScale = Vector3.one * 0.001f;
        var panelPosition = new Vector3(0.5f, 1.3f, 0.9f);
        canvasGo.transform.position = panelPosition;
        canvasGo.transform.rotation = Quaternion.LookRotation(panelPosition - new Vector3(0f, 1.5f, 0f));

        var textGo = new GameObject("Status Text", typeof(TextMeshProUGUI));
        textGo.transform.SetParent(canvasGo.transform, false);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(14f, 14f);
        text.rectTransform.offsetMax = new Vector2(-14f, -14f);
        text.fontSize = 24f;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.text = "ROS status…";

        var statusUi = canvasGo.AddComponent<RosConnectionStatusUI>();
        var statusSo = new SerializedObject(statusUi);
        statusSo.FindProperty("_jointStateSub").objectReferenceValue = robot.GetComponentInChildren<RosSubscriberJointState>();
        statusSo.FindProperty("_statusText").objectReferenceValue = text;
        statusSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, k_ScenePath);
        AddToBuildSettings(k_ScenePath);

        const string nextSteps =
            "MVP scene created and added to Build Settings.\n\n" +
            "Finish with Meta Building Blocks (Meta > Tools > Building Blocks):\n" +
            "1. Drag in [Camera Rig] and [Passthrough]\n" +
            "2. Optional: [Hand Tracking]\n" +
            "3. To move the robot: give 'Robot Placement Handle' a grab interaction " +
            "(Interaction SDK quick action or [Grab Interaction] block)\n" +
            "4. Run Meta > Tools > Project Setup Tool and Fix All for Android\n\n" +
            "Full guide: Docs/MVP_QUEST3_SETUP.md";
        Debug.Log($"[XRViz] {nextSteps}");
        EditorUtility.DisplayDialog("XRViz MVP Scene", nextSteps, "OK");
    }

    static void AddToBuildSettings(string scenePath)
    {
        if (EditorBuildSettings.scenes.Any(s => s.path == scenePath))
            return;
        EditorBuildSettings.scenes = EditorBuildSettings.scenes
            .Append(new EditorBuildSettingsScene(scenePath, true))
            .ToArray();
    }
}
