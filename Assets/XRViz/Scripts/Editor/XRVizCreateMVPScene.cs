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
using Oculus.Interaction.Surfaces;

public static class XRVizCreateMVPScene
{
    const string k_ScenePath = "Assets/XRViz/Scenes/MVP_UR3e_MR.unity";
    const string k_RobotPrefabGuid = "5e41957997504bf458dbb54a5403d950"; // ur3e_rg2.prefab
    const string k_RayCanvasTemplateGuid = "8369d93f7b6b99742bbea0649a41b7b1"; // Template_RayInteraction.prefab - already has RayInteractable + PointableCanvas wired together
    const string k_HandGrabTemplateGuid = "6ee61821e0d5b094a8d732834b365b21"; // ISDK_HandGrabInteraction - near-field grab
    const string k_RayGrabTemplateGuid = "f1c9f92f4fa1883459a0dfa57e136262";  // ISDK_RayGrabInteraction - grab at a distance
    const string k_RobotObjectName = "UR3e Robot";
    static readonly string[] k_GeneratedRootObjectNames =
    {
        "Directional Light", k_RobotObjectName, "Robot Placement Handle",
        "ROS Control Panel", "Panel Placement Handle",
        "ROS Status Panel", // legacy: was a root object before the panel group was introduced
    };
    // Canvases are authored in 400x300-style UI units and scaled down to metres
    const float k_PanelUnitsToMeters = 0.001f;
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
        AddGrabInteraction(handle, handle.transform);

        // World-space ROS control panel: the status panel and the IP keypad are sibling
        // Canvases under a plain Transform, NOT one Canvas nested inside the other.
        // PointableCanvasModule.FindFirstRaycastWithinCanvas discards any raycast hit whose
        // Canvas.rootCanvas isn't the exact Canvas injected into the PointableCanvas, and a
        // nested Canvas reports its outermost ancestor as its rootCanvas - so nesting the
        // keypad inside the status panel silently killed every keypad button hit (and made
        // the module's canvas.worldCamera assignment a no-op). Keep them siblings.
        var panelPosition = new Vector3(0.5f, 1.3f, 0.9f);
        var panelRootGo = new GameObject("ROS Control Panel");
        panelRootGo.transform.SetPositionAndRotation(
            panelPosition, Quaternion.LookRotation(panelPosition - new Vector3(0f, 1.5f, 0f)));

        var canvasGo = new GameObject("ROS Status Panel", typeof(Canvas));
        canvasGo.transform.SetParent(panelRootGo.transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(400f, 300f);
        canvasGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;

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

        var jointStateSub = robot.GetComponentInChildren<RosSubscriberJointState>();

        var statusUi = canvasGo.AddComponent<RosConnectionStatusUI>();
        var statusSo = new SerializedObject(statusUi);
        statusSo.FindProperty("_jointStateSub").objectReferenceValue = jointStateSub;
        statusSo.FindProperty("_statusText").objectReferenceValue = text;
        statusSo.ApplyModifiedPropertiesWithoutUndo();

        var connectButton = CreateButton(canvasGo.transform, "Connect", new Vector2(-105f, 14f), new Vector2(190f, 50f));
        UnityEventTools.AddVoidPersistentListener(connectButton.onClick, statusUi.Connect);
        var disconnectButton = CreateButton(canvasGo.transform, "Disconnect", new Vector2(105f, 14f), new Vector2(190f, 50f));
        UnityEventTools.AddVoidPersistentListener(disconnectButton.onClick, statusUi.Disconnect);
        var editIpButton = CreateButton(canvasGo.transform, "Edit IP", new Vector2(-105f, 74f), new Vector2(190f, 50f));
        var topicsButton = CreateButton(canvasGo.transform, "Topics", new Vector2(105f, 74f), new Vector2(190f, 50f));

        var keypadUi = CreateIpKeypad(panelRootGo.transform, statusUi);
        UnityEventTools.AddVoidPersistentListener(editIpButton.onClick, keypadUi.ToggleVisibility);

        var topicBrowserUi = CreateTopicBrowser(panelRootGo.transform, jointStateSub);
        UnityEventTools.AddVoidPersistentListener(topicsButton.onClick, topicBrowserUi.ToggleVisibility);

        AddRayInteractionToCanvas(canvas);
        AddRayInteractionToCanvas(keypadUi.GetComponent<Canvas>());
        AddRayInteractionToCanvas(topicBrowserUi.GetComponent<Canvas>());

        // Panel starts hidden; left controller Menu button (OVRInput.Button.Start on LTouch)
        // toggles it, so it doesn't just float in view permanently. Lives on the group root
        // (which stays active, so it keeps polling) and toggles the child Canvases.
        panelRootGo.AddComponent<ControlPanelMenuToggle>();

        // Grab handle beside the panel; make it grabbable with the Interaction SDK
        // (see Docs/MVP_QUEST3_SETUP.md) and the panel follows, same as the robot handle
        var panelHandle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panelHandle.name = "Panel Placement Handle";
        panelHandle.transform.localScale = Vector3.one * 0.08f;
        panelHandle.transform.position = panelPosition + new Vector3(0f, -0.16f, 0f);
        var panelFollower = panelHandle.AddComponent<PanelPlacementFollower>();
        var panelFollowerSo = new SerializedObject(panelFollower);
        panelFollowerSo.FindProperty("_panel").objectReferenceValue = panelRootGo.transform;
        panelFollowerSo.FindProperty("_panelOffset").vector3Value = new Vector3(0f, 0.16f, 0f);
        panelFollowerSo.ApplyModifiedPropertiesWithoutUndo();
        AddGrabInteraction(panelHandle, panelHandle.transform);

        EditorSceneManager.SaveScene(scene, k_ScenePath);
        AddToBuildSettings(k_ScenePath);

        const string nextSteps =
            "MVP scene updated and added to Build Settings. Re-running this only replaces the " +
            "objects it generates (robot, handles, ROS panel) - Camera Rig, Passthrough, and any " +
            "other Building Blocks you've added are left alone and don't need to be re-added.\n\n" +
            "Robot Placement Handle and Panel Placement Handle are both movable out of the box - " +
            "near grab (reach out and grab the cube) AND ray grab (point at it from a distance " +
            "and hold the trigger); the robot/panel follows its handle either way. The panel's " +
            "Connect/Disconnect/Edit IP/Topics buttons are ray-enabled (point + trigger, like a normal menu). " +
            "Edit IP shows/hides a numeric keypad (no native VR keyboard is installed) to retype the " +
            "ROS IP address and reconnect at runtime. Topics shows/hides a browser that asks the " +
            "endpoint what it's advertising and lets you re-point the joint-state subscriber at a " +
            "different topic without leaving the headset. The panel itself starts hidden and toggles " +
            "with the left controller's Menu button.\n\n" +
            "These only work once the interactor rig exists, via Meta Building Blocks " +
            "(Meta > Tools > Building Blocks) — add each ONCE per project, not per scene:\n" +
            "1. [Camera Rig] and [Passthrough]\n" +
            "2. [Grab Interaction] (near grab on both placement handles)\n" +
            "3. [Ray Interaction] (the panel's buttons, and ray grab on both handles)\n" +
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

    static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, Vector2 anchoredPosition, Vector2 sizeDelta, float fontSize)
    {
        var go = new GameObject(name, typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = TextAlignmentOptions.Center;
        var rt = label.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = sizeDelta;
        return label;
    }

    // Popup topic browser: asks the endpoint what it's advertising and lets one be picked to
    // subscribe to, from inside the headset. Its own root Canvas, sibling to the status panel
    // (see the note at the panel group on why these must not nest), sitting to its left.
    // Paged rather than scrolled - a ScrollRect is fiddly to hit with a ray, and fixed rows
    // need no viewport mask, layout group or content size fitter.
    static TopicBrowserUI CreateTopicBrowser(Transform parent, RosSubscriberJointState subscriber)
    {
        const int rowCount = 8;

        var browserGo = new GameObject("Topic Browser Panel", typeof(Canvas));
        browserGo.transform.SetParent(parent, false);
        var browserCanvas = browserGo.GetComponent<Canvas>();
        browserCanvas.renderMode = RenderMode.WorldSpace;
        var browserRect = browserGo.GetComponent<RectTransform>();
        browserRect.sizeDelta = new Vector2(420f, 540f);
        // To the left of the 400-unit-wide status panel, with a small gap (the keypad is right)
        browserGo.transform.localPosition = new Vector3(-445f * k_PanelUnitsToMeters, 0f, 0f);
        browserGo.transform.localRotation = Quaternion.identity;
        browserGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
        browserGo.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.95f);

        var title = CreateLabel(browserGo.transform, "Title", "Topics", new Vector2(0f, 240f), new Vector2(400f, 36f), 24f);
        var status = CreateLabel(browserGo.transform, "Status", "", new Vector2(0f, 205f), new Vector2(400f, 30f), 18f);
        var pageLabel = CreateLabel(browserGo.transform, "Page Label", "1 / 1", new Vector2(0f, -195f), new Vector2(120f, 44f), 18f);

        var browserUi = browserGo.AddComponent<TopicBrowserUI>();

        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(browserGo.transform, "", new Vector2(0f, 160f - i * 44f), new Vector2(390f, 40f), 17f, centerAnchored: true);
            rows[i].gameObject.name = $"Topic Row {i}";
            // Topic names read better left-aligned, and they're long enough to want the room
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(12f, 0f);
        }

        var prevButton = CreateButton(browserGo.transform, "◀ Prev", new Vector2(-130f, -195f), new Vector2(120f, 44f), 18f, centerAnchored: true);
        UnityEventTools.AddVoidPersistentListener(prevButton.onClick, browserUi.PreviousPage);
        var nextButton = CreateButton(browserGo.transform, "Next ▶", new Vector2(130f, -195f), new Vector2(120f, 44f), 18f, centerAnchored: true);
        UnityEventTools.AddVoidPersistentListener(nextButton.onClick, browserUi.NextPage);

        var refreshButton = CreateButton(browserGo.transform, "Refresh", new Vector2(-85f, -243f), new Vector2(150f, 44f), 18f, centerAnchored: true);
        UnityEventTools.AddVoidPersistentListener(refreshButton.onClick, browserUi.Refresh);
        var closeButton = CreateButton(browserGo.transform, "Close", new Vector2(85f, -243f), new Vector2(150f, 44f), 18f, centerAnchored: true);
        UnityEventTools.AddVoidPersistentListener(closeButton.onClick, browserUi.ToggleVisibility);

        var browserSo = new SerializedObject(browserUi);
        browserSo.FindProperty("_target").objectReferenceValue = subscriber;
        browserSo.FindProperty("_title").objectReferenceValue = title;
        browserSo.FindProperty("_status").objectReferenceValue = status;
        browserSo.FindProperty("_pageLabel").objectReferenceValue = pageLabel;
        var rowsProp = browserSo.FindProperty("_rows");
        rowsProp.arraySize = rowCount;
        for (int i = 0; i < rowCount; i++)
            rowsProp.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
        browserSo.ApplyModifiedPropertiesWithoutUndo();

        browserGo.SetActive(false);
        return browserUi;
    }

    // Standalone popup keypad for retyping the ROS IP address at runtime - its own root Canvas,
    // sibling to the status panel under the panel group root (see the note there on why it must
    // not be nested inside the status Canvas) so it still moves along with the panel. Shown and
    // hidden independently via the panel's "Edit IP" button, since there's no native VR keyboard
    // installed in this project's packages to hook a TMP_InputField up to.
    // `parent` is the group root, so localPosition here is in metres, not canvas UI units.
    static IpKeypadUI CreateIpKeypad(Transform parent, RosConnectionStatusUI statusUi)
    {
        var keypadGo = new GameObject("IP Keypad Panel", typeof(Canvas));
        keypadGo.transform.SetParent(parent, false);
        var keypadCanvas = keypadGo.GetComponent<Canvas>();
        keypadCanvas.renderMode = RenderMode.WorldSpace;
        var keypadRect = keypadGo.GetComponent<RectTransform>();
        keypadRect.sizeDelta = new Vector2(320f, 420f);
        // Sits to the right of the 400-unit-wide status panel with a small gap between them
        keypadGo.transform.localPosition = new Vector3(430f * k_PanelUnitsToMeters, 0f, 0f);
        keypadGo.transform.localRotation = Quaternion.identity;
        keypadGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
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

    // Makes a placement handle movable two ways, so it never has to be walked over to:
    //   near grab - reach out and grab it, with hands or controllers
    //   ray grab  - point at it from across the room and pull the trigger
    // Both feed the same Grabbable, which is what actually moves targetTransform. Mirrors the
    // Interaction SDK's "Add Grab Interaction" and "Add Ray Grab Interaction" quick actions
    // (Oculus.Interaction.Editor.QuickActions.GrabWizard / RayGrabWizard), reusing the same
    // templates they instantiate.
    static void AddGrabInteraction(GameObject target, Transform targetTransform)
    {
        var rb = target.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;

        var grabbable = target.AddComponent<Grabbable>();
        grabbable.InjectOptionalTargetTransform(targetTransform);

        // Resolve the collider before the interaction templates are parented in, so ray grab's
        // hit test uses the handle's own collider rather than one that came in with a template
        var collider = target.GetComponentInChildren<Collider>();
        if (collider == null)
        {
            var rectTransform = target.GetComponent<RectTransform>();
            var box = target.AddComponent<BoxCollider>();
            if (rectTransform != null)
                box.size = new Vector3(rectTransform.rect.width, rectTransform.rect.height, 20f);
            collider = box;
        }

        var grabGo = InstantiateInteractionTemplate(
            k_HandGrabTemplateGuid, target.transform, "Grab Interaction", "Near-grab");
        if (grabGo != null)
        {
            var handInteractable = grabGo.GetComponent<HandGrabInteractable>();
            handInteractable.InjectRigidbody(rb);
            handInteractable.InjectSupportedGrabTypes(GrabTypeFlags.All);
            handInteractable.InjectOptionalPointableElement(grabbable);

            var grabInteractable = grabGo.GetComponent<GrabInteractable>();
            grabInteractable.InjectRigidbody(rb);
            grabInteractable.InjectOptionalPointableElement(grabbable);
        }

        // The ray grab template carries a RayInteractable already wired to a movement provider,
        // but with no hit-test surface - wrap the handle's collider in one, as RayGrabWizard does
        var rayGrabGo = InstantiateInteractionTemplate(
            k_RayGrabTemplateGuid, target.transform, "Ray Grab Interaction", "Ray-grab");
        if (rayGrabGo != null)
        {
            var colliderSurface = rayGrabGo.AddComponent<ColliderSurface>();
            colliderSurface.InjectCollider(collider);

            var rayInteractable = rayGrabGo.GetComponent<RayInteractable>();
            rayInteractable.InjectSurface(colliderSurface);
            rayInteractable.InjectOptionalPointableElement(grabbable);
        }
    }

    static GameObject InstantiateInteractionTemplate(string guid, Transform parent, string name, string description)
    {
        string templatePath = AssetDatabase.GUIDToAssetPath(guid);
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(templatePath);
        if (template == null)
        {
            Debug.LogWarning($"[XRViz] {description} interaction template not found (guid {guid}); the " +
                "Interaction SDK package may have changed - check the prefab guids in " +
                "com.meta.xr.sdk.interaction's Editor/QuickActions/Templates folder.");
            return null;
        }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(template, parent);
        go.name = name;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        return go;
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
