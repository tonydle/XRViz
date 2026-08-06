using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Unity.Robotics;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;

public static class XRVizCreateMVPScene
{
    const string k_ScenePath = "Assets/XRViz/Scenes/MVP_UR3e_MR.unity";
    const string k_RobotPrefabGuid = "5e41957997504bf458dbb54a5403d950"; // ur3e_rg2.prefab
    const string k_RayCanvasTemplateGuid = "8369d93f7b6b99742bbea0649a41b7b1"; // ISDK_RayCanvasInteraction
    const string k_HandGrabTemplateGuid = "6ee61821e0d5b094a8d732834b365b21"; // ISDK_HandGrabInteraction
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

        // Grab handle at the trolley's top-front-left corner (its collision box is
        // 0.71 x 1.085 x 0.73, centered at local x=0/z=0, resting on the floor at y=0 -
        // see the "Collisions" box under trolley in ur3e_rg2.prefab), nudged outward
        // so the handle sits beside the corner rather than clipping into it. Make it
        // grabbable with the Interaction SDK (see Docs/MVP_QUEST3_SETUP.md) and the robot follows
        var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        handle.name = "Robot Placement Handle";
        handle.transform.localScale = Vector3.one * 0.08f;
        var trolleyCornerOffset = new Vector3(-0.395f, 1.125f, -0.405f);
        handle.transform.position = k_RobotBasePosition + trolleyCornerOffset;
        var follower = handle.AddComponent<RobotPlacementFollower>();
        var followerSo = new SerializedObject(follower);
        followerSo.FindProperty("_robotRoot").objectReferenceValue = robotRoot;
        followerSo.FindProperty("_robotOffset").vector3Value = -trolleyCornerOffset;
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
        text.rectTransform.offsetMin = new Vector2(14f, 74f); // leave room for the buttons below
        text.rectTransform.offsetMax = new Vector2(-14f, -14f);
        text.fontSize = 24f;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.text = "ROS status…";

        var statusUi = canvasGo.AddComponent<RosConnectionStatusUI>();
        var statusSo = new SerializedObject(statusUi);
        statusSo.FindProperty("_jointStateSub").objectReferenceValue = robot.GetComponentInChildren<RosSubscriberJointState>();
        statusSo.FindProperty("_statusText").objectReferenceValue = text;
        statusSo.ApplyModifiedPropertiesWithoutUndo();

        var connectButton = CreateButton(canvasGo.transform, "Connect", new Vector2(-105f, 14f), new Vector2(190f, 50f));
        UnityEventTools.AddVoidPersistentListener(connectButton.onClick, statusUi.Connect);
        var disconnectButton = CreateButton(canvasGo.transform, "Disconnect", new Vector2(105f, 14f), new Vector2(190f, 50f));
        UnityEventTools.AddVoidPersistentListener(disconnectButton.onClick, statusUi.Disconnect);

        AddRayInteractionToCanvas(canvas);

        // Grab handle beside the panel; make it grabbable with the Interaction SDK
        // (see Docs/MVP_QUEST3_SETUP.md) and the panel follows, same as the robot handle
        var panelHandle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panelHandle.name = "Panel Placement Handle";
        panelHandle.transform.localScale = Vector3.one * 0.08f;
        panelHandle.transform.position = panelPosition + new Vector3(0f, -0.16f, 0f);
        var panelFollower = panelHandle.AddComponent<PanelPlacementFollower>();
        var panelFollowerSo = new SerializedObject(panelFollower);
        panelFollowerSo.FindProperty("_panel").objectReferenceValue = canvasGo.transform;
        panelFollowerSo.FindProperty("_panelOffset").vector3Value = new Vector3(0f, 0.16f, 0f);
        panelFollowerSo.ApplyModifiedPropertiesWithoutUndo();
        AddGrabInteraction(panelHandle, panelHandle.transform);

        EditorSceneManager.SaveScene(scene, k_ScenePath);
        AddToBuildSettings(k_ScenePath);

        const string nextSteps =
            "MVP scene created and added to Build Settings.\n\n" +
            "Robot Placement Handle and Panel Placement Handle already have grab interaction " +
            "wired up (grab the handle, the robot/panel follows), and the panel's " +
            "Connect/Disconnect buttons are ray-enabled (point + trigger, like a normal menu).\n\n" +
            "These only work once the interactor rig exists, via Meta Building Blocks " +
            "(Meta > Tools > Building Blocks) — add each ONCE per project, not per scene:\n" +
            "1. [Camera Rig] and [Passthrough]\n" +
            "2. [Grab Interaction] (drives both placement handles)\n" +
            "3. [Ray Interaction] (drives the panel's buttons)\n" +
            "4. Optional: [Hand Tracking]\n" +
            "5. Run Meta > Tools > Project Setup Tool and Fix All for Android\n\n" +
            "Full guide: Docs/MVP_QUEST3_SETUP.md";
        Debug.Log($"[XRViz] {nextSteps}");
        EditorUtility.DisplayDialog("XRViz MVP Scene", nextSteps, "OK");
    }

    static Button CreateButton(Transform parent, string label, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        var go = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = sizeDelta;
        go.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

        var textGo = new GameObject("Label", typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 20f;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;

        return go.GetComponent<Button>();
    }

    // Mirrors Oculus.Interaction.Editor.QuickActions.RayCanvasWizard's "Add Ray Interaction to
    // Canvas" quick action, using the same ISDK_RayCanvasInteraction template it instantiates.
    // Ray (point + trigger) rather than Poke (touch) so the buttons are clickable at a distance
    // without needing to physically reach through the panel.
    static void AddRayInteractionToCanvas(Canvas canvas)
    {
        if (canvas.GetComponent<GraphicRaycaster>() == null)
            canvas.gameObject.AddComponent<GraphicRaycaster>();

        if (Object.FindFirstObjectByType<PointableCanvasModule>() == null)
            new GameObject("PointableCanvasModule", typeof(EventSystem), typeof(PointableCanvasModule));

        string templatePath = AssetDatabase.GUIDToAssetPath(k_RayCanvasTemplateGuid);
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(templatePath);
        if (template == null)
        {
            Debug.LogWarning("[XRViz] Ray-canvas interaction template not found; add the 'Ray Interaction' Building Block to the project first.");
            return;
        }

        var rayGo = (GameObject)PrefabUtility.InstantiatePrefab(template, canvas.transform);
        rayGo.name = "Ray Interaction";
        var rt = rayGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = Vector2.zero;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;

        rayGo.GetComponent<PointableCanvas>().InjectCanvas(canvas);
    }

    // Mirrors Oculus.Interaction.Editor.QuickActions.GrabWizard's "Add Grab Interaction" quick
    // action, using the same ISDK_HandGrabInteraction template it instantiates.
    static void AddGrabInteraction(GameObject target, Transform targetTransform)
    {
        var rb = target.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;

        var grabbable = target.AddComponent<Grabbable>();
        grabbable.InjectOptionalTargetTransform(targetTransform);

        if (target.GetComponentInChildren<Collider>() == null)
        {
            var rectTransform = target.GetComponent<RectTransform>();
            var box = target.AddComponent<BoxCollider>();
            if (rectTransform != null)
                box.size = new Vector3(rectTransform.rect.width, rectTransform.rect.height, 20f);
        }

        string templatePath = AssetDatabase.GUIDToAssetPath(k_HandGrabTemplateGuid);
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(templatePath);
        if (template == null)
        {
            Debug.LogWarning("[XRViz] Hand-grab interaction template not found; add the 'Grab Interaction' Building Block to the project first.");
            return;
        }

        var grabGo = (GameObject)PrefabUtility.InstantiatePrefab(template, target.transform);
        grabGo.name = "Grab Interaction";
        grabGo.transform.localPosition = Vector3.zero;
        grabGo.transform.localRotation = Quaternion.identity;
        grabGo.transform.localScale = Vector3.one;

        var handInteractable = grabGo.GetComponent<HandGrabInteractable>();
        handInteractable.InjectRigidbody(rb);
        handInteractable.InjectSupportedGrabTypes(GrabTypeFlags.All);
        handInteractable.InjectOptionalPointableElement(grabbable);

        var grabInteractable = grabGo.GetComponent<GrabInteractable>();
        grabInteractable.InjectRigidbody(rb);
        grabInteractable.InjectOptionalPointableElement(grabbable);
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
