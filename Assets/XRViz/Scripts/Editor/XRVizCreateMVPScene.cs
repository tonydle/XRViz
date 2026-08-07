using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.Robotics;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;

public static class XRVizCreateMVPScene
{
    const string k_ScenePath = "Assets/XRViz/Scenes/MVP_UR3e_MR.unity";
    const string k_RobotPrefabGuid = "5e41957997504bf458dbb54a5403d950"; // ur3e_rg2.prefab
    const string k_RayCanvasTemplateGuid = "8369d93f7b6b99742bbea0649a41b7b1"; // Template_RayInteraction.prefab - already has RayInteractable + PointableCanvas wired together
    const string k_HandGrabTemplateGuid = "6ee61821e0d5b094a8d732834b365b21"; // ISDK_HandGrabInteraction
    const string k_RobotObjectName = "UR3e Robot";
    static readonly string[] k_GeneratedRootObjectNames =
    {
        "Directional Light", k_RobotObjectName, "Robot Placement Handle",
        "ROS Status Panel", "Panel Placement Handle",
    };
    static readonly Vector3 k_RobotBasePosition = new Vector3(0f, -0.7f, 0.9f);

    [MenuItem("XRViz/Create MR MVP Scene (UR3e)")]
    public static void Run()
    {
        // Update the scene in place if it already exists, rather than blowing it away and
        // starting from an empty scene - that would also wipe out Camera Rig, Passthrough,
        // and the Grab/Ray/Hand Tracking Building Blocks, which have to be re-added by hand
        // every time (Building Blocks aren't scriptable the way everything else here is).
        Scene scene;
        var openScene = EditorSceneManager.GetActiveScene();
        if (openScene.path == k_ScenePath)
        {
            scene = openScene;
            RemoveGeneratedObjects(scene);
        }
        else if (File.Exists(k_ScenePath))
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            RemoveGeneratedObjects(scene);
        }
        else
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

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
        robot.name = k_RobotObjectName;
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
        canvasRect.sizeDelta = new Vector2(400f, 300f);
        canvasGo.transform.localScale = Vector3.one * 0.001f;
        var panelPosition = new Vector3(0.5f, 1.3f, 0.9f);
        canvasGo.transform.position = panelPosition;
        canvasGo.transform.rotation = Quaternion.LookRotation(panelPosition - new Vector3(0f, 1.5f, 0f));

        var textGo = new GameObject("Status Text", typeof(TextMeshProUGUI));
        textGo.transform.SetParent(canvasGo.transform, false);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(14f, 134f); // leave room for the two button rows below
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
        var editIpButton = CreateButton(canvasGo.transform, "Edit IP", new Vector2(0f, 74f), new Vector2(190f, 50f));

        var keypadUi = CreateIpKeypad(canvasGo.transform, statusUi);
        UnityEventTools.AddVoidPersistentListener(editIpButton.onClick, keypadUi.ToggleVisibility);

        AddRayInteractionToCanvas(canvas);
        AddRayInteractionToCanvas(keypadUi.GetComponent<Canvas>());

        // Panel starts hidden; left controller Menu button (OVRInput.Button.Start on LTouch)
        // toggles it, so it doesn't just float in view permanently
        canvasGo.AddComponent<ControlPanelMenuToggle>();

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
            "MVP scene updated and added to Build Settings. Re-running this only replaces the " +
            "objects it generates (robot, handles, ROS panel) - Camera Rig, Passthrough, and any " +
            "other Building Blocks you've added are left alone and don't need to be re-added.\n\n" +
            "Robot Placement Handle and Panel Placement Handle already have grab interaction " +
            "wired up (grab the handle, the robot/panel follows), and the panel's " +
            "Connect/Disconnect/Edit IP buttons are ray-enabled (point + trigger, like a normal menu). " +
            "Edit IP shows/hides a numeric keypad (no native VR keyboard is installed) to retype the " +
            "ROS IP address and reconnect at runtime. The panel itself starts hidden and toggles " +
            "with the left controller's Menu button.\n\n" +
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

    static void RemoveGeneratedObjects(Scene scene)
    {
        foreach (var rootGo in scene.GetRootGameObjects())
        {
            if (k_GeneratedRootObjectNames.Contains(rootGo.name))
                Object.DestroyImmediate(rootGo);
        }
    }

    static Button CreateButton(Transform parent, string label, Vector2 anchoredPosition, Vector2 sizeDelta, float fontSize = 20f, bool centerAnchored = false)
    {
        var go = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        Vector2 anchor = centerAnchored ? new Vector2(0.5f, 0.5f) : new Vector2(0.5f, 0f);
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = sizeDelta;
        go.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

        var textGo = new GameObject("Label", typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = fontSize;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;

        return go.GetComponent<Button>();
    }

    // Standalone popup keypad for retyping the ROS IP address at runtime - a separate Canvas
    // (parented under the status panel so it moves along with it) that can be shown/hidden
    // independently via the panel's "Edit IP" button, since there's no native VR keyboard
    // installed in this project's packages to hook a TMP_InputField up to.
    static IpKeypadUI CreateIpKeypad(Transform parent, RosConnectionStatusUI statusUi)
    {
        var keypadGo = new GameObject("IP Keypad Panel", typeof(Canvas));
        keypadGo.transform.SetParent(parent, false);
        var keypadCanvas = keypadGo.GetComponent<Canvas>();
        keypadCanvas.renderMode = RenderMode.WorldSpace;
        var keypadRect = keypadGo.GetComponent<RectTransform>();
        keypadRect.sizeDelta = new Vector2(320f, 420f);
        keypadGo.transform.localPosition = new Vector3(430f, 0f, 0f);
        keypadGo.transform.localRotation = Quaternion.identity;
        keypadGo.transform.localScale = Vector3.one;
        keypadGo.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.95f);

        var displayGo = new GameObject("Display", typeof(TextMeshProUGUI));
        displayGo.transform.SetParent(keypadGo.transform, false);
        var display = displayGo.GetComponent<TextMeshProUGUI>();
        display.fontSize = 26f;
        display.alignment = TextAlignmentOptions.Center;
        var displayRect = display.rectTransform;
        displayRect.anchorMin = new Vector2(0.5f, 1f);
        displayRect.anchorMax = new Vector2(0.5f, 1f);
        displayRect.pivot = new Vector2(0.5f, 1f);
        displayRect.anchoredPosition = new Vector2(0f, -14f);
        displayRect.sizeDelta = new Vector2(290f, 50f);

        var keypadUi = keypadGo.AddComponent<IpKeypadUI>();
        var keypadSo = new SerializedObject(keypadUi);
        keypadSo.FindProperty("_display").objectReferenceValue = display;
        keypadSo.FindProperty("_statusUi").objectReferenceValue = statusUi;
        keypadSo.ApplyModifiedPropertiesWithoutUndo();

        string[,] grid =
        {
            { "1", "2", "3" },
            { "4", "5", "6" },
            { "7", "8", "9" },
            { ".", "0", "back" },
        };
        for (int row = 0; row < 4; row++)
        {
            float y = 85f - row * 60f;
            for (int col = 0; col < 3; col++)
            {
                float x = -100f + col * 100f;
                string key = grid[row, col];
                bool isBackspace = key == "back";
                var keyButton = CreateButton(keypadGo.transform, isBackspace ? "⌫" : key, new Vector2(x, y), new Vector2(90f, 50f), 20f, centerAnchored: true);
                if (isBackspace)
                    UnityEventTools.AddVoidPersistentListener(keyButton.onClick, keypadUi.Backspace);
                else
                    UnityEventTools.AddStringPersistentListener(keyButton.onClick, keypadUi.AppendChar, key);
            }
        }

        var clearButton = CreateButton(keypadGo.transform, "Clear", new Vector2(-80f, -155f), new Vector2(140f, 50f), 18f, centerAnchored: true);
        UnityEventTools.AddVoidPersistentListener(clearButton.onClick, keypadUi.Clear);

        var applyButton = CreateButton(keypadGo.transform, "Apply", new Vector2(80f, -155f), new Vector2(140f, 50f), 18f, centerAnchored: true);
        UnityEventTools.AddVoidPersistentListener(applyButton.onClick, keypadUi.Apply);

        keypadGo.SetActive(false);
        return keypadUi;
    }

    // Ray (point + trigger) rather than Poke (touch) so the buttons are clickable at a distance
    // without needing to physically reach through the panel. Uses the Interaction SDK's
    // Template_RayInteraction.prefab (guid below), which already bundles a RayInteractable
    // (with its own hit-test surface) wired to a PointableCanvas with an empty _canvas field
    // ready for injection - this is the same prefab Oculus.Interaction.Editor.QuickActions.
    // RayCanvasWizard's "Add Ray Interaction to Canvas" quick action instantiates.
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
            Debug.LogWarning("[XRViz] Ray interaction template not found (guid " + k_RayCanvasTemplateGuid + "); the Interaction SDK package may have changed - check Template_RayInteraction.prefab's guid in com.meta.xr.sdk.interaction's QuickActions/Templates folder.");
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
