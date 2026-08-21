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
        "Laser Scan", "Laser Scan Placement Handle",
        "Point Cloud", "Point Cloud Placement Handle",
        "TF Origin", "TF Origin Placement Handle",
        "ROS Status Panel", // legacy: was a root object before the panel group was introduced
    };
    const string k_PointCloudComputePath = "Assets/XRViz/Shaders/DepthImagePointCloudGPU.compute";
    // Canvases are authored in 400x300-style UI units and scaled down to metres
    const float k_PanelUnitsToMeters = 0.001f;
    static readonly Vector3 k_RobotBasePosition = new Vector3(0f, -0.7f, 0.9f);

    // --- Placement handles ---------------------------------------------------------------
    //
    // Spheres rather than cubes: a cube's silhouette changes with every rotation, so at 4 cm
    // across it reads as a different object depending on where you stand. A sphere looks the
    // same from everywhere, which is what you want from a thing you're supposed to find and
    // grab. Small, too - a handle is a grip, not a landmark, and a big one occludes the robot
    // it's attached to.
    //
    // Colour is the only thing telling three otherwise identical spheres apart, so the same
    // definition drives both the handle material and the panel's key. Chosen to stay distinct
    // for the common red-green colour deficiencies (amber / cyan / magenta rather than
    // red / green / blue) and to sit away from the greys and skin tones passthrough is full of.
    struct HandleStyle
    {
        public string ObjectName; // scene object name; also names the generated material
        public string Label;      // what the panel's key calls it
        public Color Color;
    }

    static readonly HandleStyle k_RobotHandleStyle = new HandleStyle
    {
        ObjectName = "Robot Placement Handle",
        Label = "Robot",
        Color = new Color(1f, 0.58f, 0.13f), // amber
    };
    static readonly HandleStyle k_PanelHandleStyle = new HandleStyle
    {
        ObjectName = "Panel Placement Handle",
        Label = "Control panel",
        Color = new Color(0.20f, 0.75f, 1f), // cyan
    };
    static readonly HandleStyle k_ScanHandleStyle = new HandleStyle
    {
        ObjectName = "Laser Scan Placement Handle",
        Label = "Laser scan",
        Color = new Color(0.85f, 0.33f, 0.95f), // magenta
    };
    static readonly HandleStyle k_PointCloudHandleStyle = new HandleStyle
    {
        ObjectName = "Point Cloud Placement Handle",
        Label = "Point cloud origin",
        Color = new Color(0.35f, 0.90f, 0.45f), // green
    };
    // White and a size up from the rest, because this one isn't a peer of the others: it is the
    // origin of the TF world, and while TF anchoring is on it is the only handle still visible -
    // every anchored visualisation is measured out from it, so moving it moves all of them.
    static readonly HandleStyle k_TfOriginHandleStyle = new HandleStyle
    {
        ObjectName = "TF Origin Placement Handle",
        Label = "TF origin (fixed frame)",
        Color = new Color(0.96f, 0.96f, 0.98f), // white
    };
    static readonly HandleStyle[] k_HandleStyles =
    {
        k_RobotHandleStyle, k_PanelHandleStyle, k_ScanHandleStyle, k_PointCloudHandleStyle,
        k_TfOriginHandleStyle,
    };

    const string k_MaterialFolder = "Assets/XRViz/Materials";
    const string k_HandleShaderName = "XRViz/HandleUnlit";

    // --- Panel palette -------------------------------------------------------------------
    //
    // Dark and near-opaque on purpose. These canvases float over passthrough video of a real
    // room; a translucent panel against a white wall or a window makes light text unreadable,
    // which is the whole reason a background is here at all.
    static readonly Color k_PanelBackground = new Color(0.06f, 0.07f, 0.09f, 0.95f);
    static readonly Color k_PanelHeader = new Color(0.11f, 0.33f, 0.52f, 1f);
    static readonly Color k_Divider = new Color(1f, 1f, 1f, 0.12f);
    static readonly Color k_TextPrimary = new Color(0.93f, 0.95f, 0.97f);
    static readonly Color k_TextMuted = new Color(0.62f, 0.67f, 0.73f);
    static readonly Color k_ButtonNeutral = new Color(0.19f, 0.21f, 0.25f);
    static readonly Color k_ButtonAccent = new Color(0.15f, 0.31f, 0.50f);
    static readonly Color k_ButtonPositive = new Color(0.13f, 0.40f, 0.22f);
    static readonly Color k_ButtonNegative = new Color(0.44f, 0.16f, 0.16f);

    const float k_HeaderHeight = 54f;
    // Tall enough for the status block, a key row per handle, five button rows and the
    // feedback line - see the layout constants in Run(), which are all measured from the centre
    static readonly Vector2 k_StatusPanelSize = new Vector2(420f, 752f);
    static readonly Vector2 k_ButtonSize = new Vector2(190f, 50f);

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
        var trolleyCornerOffset = new Vector3(-0.395f, 1.125f, -0.405f);
        var robotHandle = CreatePlacementHandle(k_RobotHandleStyle, k_RobotBasePosition + trolleyCornerOffset,
            0.04f, target: null, articulationBody: robotRoot, offset: -trolleyCornerOffset);

        // The TF world's origin: what every TF-anchored visualisation is measured out from, and
        // therefore the one thing that has to line up with the real room for all of them to land
        // correctly. Starts on the robot's base, since that is where the fixed frame of an arm rig
        // physically is - align it with the real robot's base and the rest follows from /tf.
        //
        // Its handle sits on the opposite top corner of the trolley from the robot's, so the two
        // are never confused for each other by position alone (they differ by colour and size too).
        var tfOriginGo = new GameObject("TF Origin");
        tfOriginGo.transform.SetPositionAndRotation(k_RobotBasePosition, Quaternion.identity);
        tfOriginGo.AddComponent<RosTfTree>();
        CreateAxisTriad(tfOriginGo.transform, length: 0.15f, thickness: 0.008f);

        var tfHandleOffset = new Vector3(0.395f, 1.125f, 0.405f);
        CreatePlacementHandle(k_TfOriginHandleStyle, k_RobotBasePosition + tfHandleOffset,
            0.05f, target: tfOriginGo.transform, articulationBody: null, offset: -tfHandleOffset);

        // The robot's own base link is a TF frame like any other, so the robot can be placed from
        // /tf too - which is what makes a mobile base (odom -> base_link) move in the room rather
        // than staying wherever it was dropped. No frame source: no message carries the robot's
        // root frame name, so it is named here. Retype it in the Inspector for a robot whose root
        // link isn't the conventional base_link.
        AddTfAnchor(robot, robotHandle, frameSource: null, frameId: "base_link", label: "Robot");
        AddVisibilityTarget(robot, "Robot");

        // Laser scan visualisation. The scan mesh is built in the sensor's own frame under this
        // object's Transform, so its handle moves it like anything else - point the topic browser
        // at whatever /scan-alike the robot actually publishes.
        var scanPosition = new Vector3(-0.8f, 1.0f, 1.2f);
        var scanGo = new GameObject("Laser Scan", typeof(MeshFilter), typeof(MeshRenderer));
        scanGo.transform.position = scanPosition;
        var scanSub = scanGo.AddComponent<RosSubscriberLaserScan>();
        var scanSubSo = new SerializedObject(scanSub);
        // No default topic - Subscribe() no-ops on an empty string, so this stays unsubscribed
        // until a topic is picked from the panel's Topics browser, which only ever lists what
        // the endpoint actually advertises.
        scanSubSo.FindProperty("_topic").stringValue = "";
        scanSubSo.ApplyModifiedPropertiesWithoutUndo();
        var scanVis = scanGo.AddComponent<LaserScanVisualizer>();
        var scanVisSo = new SerializedObject(scanVis);
        scanVisSo.FindProperty("_scanSub").objectReferenceValue = scanSub;
        scanVisSo.ApplyModifiedPropertiesWithoutUndo();

        var scanHandle = CreatePlacementHandle(k_ScanHandleStyle, scanPosition + new Vector3(0f, -0.15f, 0f),
            0.03f, target: scanGo.transform, articulationBody: null, offset: new Vector3(0f, 0.15f, 0f));

        // Frame comes from the scan's own header, so retargeting the topic from the headset
        // retargets the anchor with it - there is no keyboard in there to retype a frame name
        AddTfAnchor(scanGo, scanHandle, scanSub, frameId: string.Empty, label: "Laser scan");
        AddVisibilityTarget(scanGo, "Laser Scan");

        // RGBD point cloud. Its Transform IS the cloud's origin - the camera's optical centre
        // sits on this object and the cloud projects out along its +Z - so placing the handle
        // where the real camera stands in the room lands the virtual geometry on the real
        // geometry. Aimed back at the robot to start with, which is the usual thing to look at.
        var cloudPosition = new Vector3(0.2f, 1.2f, 0.50f);
        // Yaw only, pitch and roll zeroed - the cloud's floor has to stay parallel to the room's,
        // so aiming is levelled off toward the robot rather than looking down/up at it.
        var cloudLookDir = (k_RobotBasePosition + new Vector3(0f, 1.1f, 0f)) - cloudPosition;
        cloudLookDir.y = 0f;
        var cloudRotation = Quaternion.LookRotation(cloudLookDir, Vector3.up);
        var cloudGo = new GameObject("Point Cloud");
        cloudGo.transform.SetPositionAndRotation(cloudPosition, cloudRotation);

        // Raw sensor_msgs/Image on child objects rather than three components stacked on the
        // cloud root: two of them are the same type, and in the Inspector (and in the topic
        // browser's target list) "Color Image" and "Depth Image" are the only thing that tells
        // them apart at a glance. No default topic - see the note on the laser scan above.
        var colorSub = CreateImageSubscriber(cloudGo.transform, "Color Image", "");
        var depthSub = CreateImageSubscriber(cloudGo.transform, "Depth Image", "");

        var infoGo = new GameObject("Depth Camera Info");
        infoGo.transform.SetParent(cloudGo.transform, false);
        var depthInfoSub = infoGo.AddComponent<RosSubscriberCameraInfo>();
        var infoSo = new SerializedObject(depthInfoSub);
        infoSo.FindProperty("_topic").stringValue = "";
        infoSo.ApplyModifiedPropertiesWithoutUndo();

        var cloud = cloudGo.AddComponent<DepthImagePointCloud>();
        var cloudSo = new SerializedObject(cloud);
        cloudSo.FindProperty("_colorSub").objectReferenceValue = colorSub;
        cloudSo.FindProperty("_depthSub").objectReferenceValue = depthSub;
        cloudSo.FindProperty("_depthInfoSub").objectReferenceValue = depthInfoSub;
        var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_PointCloudComputePath);
        if (compute == null)
            Debug.LogWarning($"[XRViz] Point cloud compute shader not found at {k_PointCloudComputePath}; " +
                "assign it on the Point Cloud object by hand or the cloud will disable itself.");
        cloudSo.FindProperty("_computeShader").objectReferenceValue = compute;
        cloudSo.ApplyModifiedPropertiesWithoutUndo();

        // yawOnly (the default) keeps the cloud's floor level - only the handle's Y rotation
        // reaches the target, X and Z are always flattened to 0. The handle still starts at the
        // cloud's own (now yaw-only) rotation so the offset stays expressed in that rotated space
        // and the handle keeps sitting just below the origin as the cloud is turned.
        var cloudHandleOffset = new Vector3(0f, -0.14f, 0f);
        var cloudHandle = CreatePlacementHandle(k_PointCloudHandleStyle, cloudPosition + cloudHandleOffset, 0.035f,
            target: cloudGo.transform, articulationBody: null,
            offset: Quaternion.Inverse(cloudRotation) * -cloudHandleOffset,
            rotation: cloudRotation);

        // Anchored on the DEPTH image's frame, because depth is what gets back-projected - a
        // registered pair usually shares one optical frame, but where they differ it is the depth
        // one the geometry is actually in. That frame is right-down-forward rather than FLU, which
        // TfAnchor corrects for automatically on any *_optical_frame.
        AddTfAnchor(cloudGo, cloudHandle, depthSub, frameId: string.Empty, label: "Point cloud");
        AddVisibilityTarget(cloudGo, "Point Cloud");

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
        canvasRect.sizeDelta = k_StatusPanelSize;
        canvasGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
        StylePanel(canvasGo, "XRViz  ·  ROS Control");

        // Everything below the header is laid out from the canvas centre, so the numbers here
        // read directly as "this far above/below the middle of the panel"
        var text = CreateLabel(canvasGo.transform, "Status Text", "ROS status…",
            new Vector2(0f, 200f), new Vector2(384f, 110f), 22f, TextAlignmentOptions.TopLeft);

        CreateDivider(canvasGo.transform, 134f, 384f);
        CreateAnchorKey(canvasGo.transform, headingY: 120f, firstRowY: 92f, rowStep: 26f);
        CreateDivider(canvasGo.transform, -40f, 384f);

        var jointStateSub = robot.GetComponentInChildren<RosSubscriberJointState>();

        var statusUi = canvasGo.AddComponent<RosConnectionStatusUI>();
        var statusSo = new SerializedObject(statusUi);
        statusSo.FindProperty("_jointStateSub").objectReferenceValue = jointStateSub;
        statusSo.FindProperty("_statusText").objectReferenceValue = text;
        statusSo.ApplyModifiedPropertiesWithoutUndo();

        var actions = panelRootGo.AddComponent<ControlPanelActions>();

        var connectButton = CreateButton(canvasGo.transform, "Connect", new Vector2(-105f, -74f), k_ButtonSize, 20f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(connectButton.onClick, statusUi.Connect);
        var disconnectButton = CreateButton(canvasGo.transform, "Disconnect", new Vector2(105f, -74f), k_ButtonSize, 20f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(disconnectButton.onClick, statusUi.Disconnect);
        var editIpButton = CreateButton(canvasGo.transform, "Edit IP", new Vector2(-105f, -130f), k_ButtonSize, 20f, true, k_ButtonAccent);
        var topicsButton = CreateButton(canvasGo.transform, "Topics", new Vector2(105f, -130f), k_ButtonSize, 20f, true, k_ButtonAccent);
        var clearDataButton = CreateButton(canvasGo.transform, "Clear Data", new Vector2(-105f, -186f), k_ButtonSize, 20f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(clearDataButton.onClick, actions.ClearVisualizations);
        var resetAnchorsButton = CreateButton(canvasGo.transform, "Reset Anchors", new Vector2(105f, -186f), k_ButtonSize, 20f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(resetAnchorsButton.onClick, actions.ResetAnchors);

        // Says what pressing it gets you, not what mode you're in - ControlPanelActions rewrites
        // this label, including when the TF Anchors panel changes anchors behind its back
        var tfToggleButton = CreateButton(canvasGo.transform, "Anchor: TF", new Vector2(-105f, -242f), k_ButtonSize, 20f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(tfToggleButton.onClick, actions.ToggleTfAnchoring);
        var tfPanelButton = CreateButton(canvasGo.transform, "TF Anchors", new Vector2(105f, -242f), k_ButtonSize, 20f, true, k_ButtonNeutral);

        // Opens the per-visualisation show/hide list - single centered button, since there's
        // only one action here (the panel itself carries Show All / Hide All)
        var visibilityPanelButton = CreateButton(canvasGo.transform, "Visibility", new Vector2(0f, -298f), new Vector2(200f, 50f), 20f, true, k_ButtonAccent);

        var feedback = CreateLabel(canvasGo.transform, "Action Feedback", "",
            new Vector2(0f, -342f), new Vector2(384f, 26f), 17f);
        feedback.color = k_TextMuted;
        var actionsSo = new SerializedObject(actions);
        actionsSo.FindProperty("_feedback").objectReferenceValue = feedback;
        actionsSo.FindProperty("_tfButtonLabel").objectReferenceValue =
            tfToggleButton.GetComponentInChildren<TextMeshProUGUI>();
        actionsSo.ApplyModifiedPropertiesWithoutUndo();

        var keypadUi = CreateIpKeypad(panelRootGo.transform, statusUi);
        UnityEventTools.AddVoidPersistentListener(editIpButton.onClick, keypadUi.ToggleVisibility);

        var topicBrowserUi = CreateTopicBrowser(panelRootGo.transform,
            new MonoBehaviour[] { jointStateSub, scanSub, colorSub, depthSub, depthInfoSub });
        UnityEventTools.AddVoidPersistentListener(topicsButton.onClick, topicBrowserUi.ToggleVisibility);

        var tfAnchorPanelUi = CreateTfAnchorPanel(panelRootGo.transform);
        UnityEventTools.AddVoidPersistentListener(tfPanelButton.onClick, tfAnchorPanelUi.ToggleVisibility);

        var visibilityPanelUi = CreateVisibilityPanel(panelRootGo.transform);
        UnityEventTools.AddVoidPersistentListener(visibilityPanelButton.onClick, visibilityPanelUi.ToggleVisibility);

        AddRayInteractionToCanvas(canvas);
        AddRayInteractionToCanvas(keypadUi.GetComponent<Canvas>());
        AddRayInteractionToCanvas(topicBrowserUi.GetComponent<Canvas>());
        AddRayInteractionToCanvas(tfAnchorPanelUi.GetComponent<Canvas>());
        AddRayInteractionToCanvas(visibilityPanelUi.GetComponent<Canvas>());

        // Panel starts hidden; left controller Menu button (OVRInput.Button.Start on LTouch)
        // toggles it, so it doesn't just float in view permanently. Lives on the group root
        // (which stays active, so it keeps polling) and toggles the child Canvases.
        panelRootGo.AddComponent<ControlPanelMenuToggle>();

        // Grab handle below the panel; the panel follows, same as the robot handle. The offset
        // clears the panel's own half-height (752 units x 0.001 = 75.2 cm tall) plus a small gap.
        CreatePlacementHandle(k_PanelHandleStyle, panelPosition + new Vector3(0f, -0.436f, 0f),
            0.04f, target: panelRootGo.transform, articulationBody: null, offset: new Vector3(0f, 0.436f, 0f));

        EditorSceneManager.SaveScene(scene, k_ScenePath);
        AddToBuildSettings(k_ScenePath);

        const string nextSteps =
            "MVP scene updated and added to Build Settings. Re-running this only replaces the " +
            "objects it generates (robot, handles, ROS panel, laser scan) - Camera Rig, Passthrough, " +
            "and any other Building Blocks you've added are left alone and don't need to be re-added.\n\n" +
            "Every placement handle is a small coloured sphere - amber for the robot, cyan for the " +
            "control panel, magenta for the laser scan, green for the point cloud origin, white (and " +
            "a size up) for the TF origin, with a key on the panel itself. Each is movable out of the " +
            "box: near grab (reach out and grab it) AND ray grab (point at it from a distance and hold " +
            "the trigger); whatever it's pointed at follows either way.\n\n" +

            "TF ANCHORING. Press 'Anchor: TF' on the panel and the robot, laser scan and point cloud " +
            "stop being placed by hand and are placed from /tf instead - each at its own frame, all " +
            "consistent with each other. Their handles hide while that's on, because TF owns the pose; " +
            "the WHITE TF origin handle stays, and it is now the only thing to align: put its small " +
            "red/green/blue axis triad (ROS x/y/z) on the real robot's base and everything else lands " +
            "where /tf says it is. Press again for hand " +
            "placement, and each handle comes back where TF left its object. 'TF Anchors' opens a " +
            "panel showing each visualisation's frame and whether it resolved, one row per press to " +
            "flip just that one. Frames come from each topic's own header, so retargeting a topic " +
            "retargets its anchor - except the robot's, which is typed on its TfAnchor component " +
            "(default base_link).\n\n" +
            "THE POINT CLOUD'S HANDLE IS ITS ORIGIN. The camera's optical centre sits on that green " +
            "sphere and the cloud projects out along its +Z, so park the handle where the real " +
            "camera stands in the room and the virtual geometry lands on the real geometry. It is " +
            "the one handle that takes full rotation rather than yaw only, because a camera has to " +
            "be aimed. None of the laser scan or point cloud subscribers start with a default " +
            "topic - pick color, depth and depth camera_info from the Topics browser before " +
            "either visualisation has anything to draw.\n\n" +
            "The panel's buttons are ray-enabled (point + trigger, like a normal menu):\n" +
            "  Connect / Disconnect - the ROS TCP connection\n" +
            "  Edit IP - shows a numeric keypad (no native VR keyboard is installed) to retype the " +
            "ROS IP and reconnect at runtime\n" +
            "  Topics - shows a browser that asks the endpoint what it's advertising and re-points a " +
            "subscriber at a different topic without leaving the headset; its ◀ ▶ picks which " +
            "subscriber, and the list is filtered to that subscriber's message type\n" +
            "  Clear Data - wipes every visualisation holding geometry (laser scan, point cloud). " +
            "They also clear themselves after 3 s without a message, so a dropped sensor doesn't " +
            "leave a stale frame hanging in the room looking live\n" +
            "  Reset Anchors - puts every placement handle, and what it carries, back where this " +
            "generator put it (including the panel itself)\n" +
            "  Anchor: TF / Anchor: Manual - switches every visualisation between /tf placement and " +
            "hand placement (the label says what pressing it gets you)\n" +
            "  TF Anchors - per-visualisation TF/manual, with the frame each one is using\n" +
            "  Visibility - show/hide the robot, laser scan and point cloud individually, or all " +
            "at once; a hidden visualisation's subscriber and anchor pause with it and pick back " +
            "up once shown again\n\n" +
            "The panel starts hidden and toggles with the left controller's Menu button.\n\n" +
            "These only work once the interactor rig exists, via Meta Building Blocks " +
            "(Meta > Tools > Building Blocks) — add each ONCE per project, not per scene:\n" +
            "1. [Camera Rig] and [Passthrough]\n" +
            "2. [Grab Interaction] (near grab on the placement handles)\n" +
            "3. [Ray Interaction] (the panel's buttons, and ray grab on the handles)\n" +
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

    // ---------------------------------------------------------------------------------------
    // UI construction helpers
    // ---------------------------------------------------------------------------------------

    // Unity's own UI sprites, which ship inside the editor and are pulled into the player build
    // when referenced. Both are 9-sliced with rounded corners, which is the entire difference
    // between "a panel" and "a rectangle of flat colour" here - there is no art in this project
    // and none needs adding for this.
    static Sprite GetBuiltinSprite(string name)
    {
        return AssetDatabase.GetBuiltinExtraResource<Sprite>($"UI/Skin/{name}.psd");
    }

    static Color Shade(Color color, float amount)
    {
        return amount >= 0f
            ? Color.Lerp(color, Color.white, amount)
            : Color.Lerp(color, Color.black, -amount);
    }

    // Gives a world-space Canvas its opaque backing and a titled header bar. Every popup here
    // floats over passthrough video of a real room, so without a background the text competes
    // with whatever is physically behind it - which on a light wall means it simply can't be read.
    // Returns the header's title label, so a panel that wants to rewrite its title at runtime
    // (the topic browser) can bind straight to it.
    static TextMeshProUGUI StylePanel(GameObject canvasGo, string title)
    {
        // The Image goes on the Canvas GameObject itself so it draws behind every child -
        // UGUI's draw order is hierarchy order, and a "background" added later would cover
        // the content it's meant to sit behind
        var background = canvasGo.GetComponent<Image>();
        if (background == null)
            background = canvasGo.AddComponent<Image>();
        background.sprite = GetBuiltinSprite("Background");
        background.type = Image.Type.Sliced;
        background.color = k_PanelBackground;
        // Left as a raycast target: it gives the ray something to land on across the whole
        // panel, so the pointer keeps a valid hit (and a stable ray endpoint) between buttons

        var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image));
        headerGo.transform.SetParent(canvasGo.transform, false);
        var headerRect = (RectTransform)headerGo.transform;
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(1f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.offsetMin = new Vector2(6f, -(k_HeaderHeight + 6f));
        headerRect.offsetMax = new Vector2(-6f, -6f);
        var headerImage = headerGo.GetComponent<Image>();
        headerImage.sprite = GetBuiltinSprite("UISprite");
        headerImage.type = Image.Type.Sliced;
        headerImage.color = k_PanelHeader;
        headerImage.raycastTarget = false;

        var titleGo = new GameObject("Title", typeof(TextMeshProUGUI));
        titleGo.transform.SetParent(headerGo.transform, false);
        var titleText = titleGo.GetComponent<TextMeshProUGUI>();
        titleText.text = title;
        titleText.fontSize = 24f;
        titleText.color = Color.white;
        titleText.alignment = TextAlignmentOptions.Center;
        titleText.raycastTarget = false;
        titleText.rectTransform.anchorMin = Vector2.zero;
        titleText.rectTransform.anchorMax = Vector2.one;
        titleText.rectTransform.offsetMin = new Vector2(12f, 0f);
        titleText.rectTransform.offsetMax = new Vector2(-12f, 0f);

        return titleText;
    }

    // Hairline rule. Purely to group the panel's three bands (status / key / buttons) - at
    // arm's length in a headset, whitespace alone doesn't separate them convincingly.
    static void CreateDivider(Transform parent, float y, float width)
    {
        var go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, y);
        rt.sizeDelta = new Vector2(width, 2f);
        var image = go.GetComponent<Image>();
        image.color = k_Divider;
        image.raycastTarget = false;
    }

    // The key for the handle spheres. Driven by the same k_HandleStyles the handles themselves
    // are built from, so the swatch colours can't drift out of sync with the things they label.
    static void CreateAnchorKey(Transform parent, float headingY, float firstRowY, float rowStep)
    {
        var heading = CreateLabel(parent, "Anchor Key Heading", "Anchor key",
            new Vector2(0f, headingY), new Vector2(384f, 24f), 19f, TextAlignmentOptions.Left);
        heading.color = k_TextMuted;

        for (int i = 0; i < k_HandleStyles.Length; i++)
        {
            var style = k_HandleStyles[i];
            float y = firstRowY - i * rowStep;

            // Knob.psd is Unity's round slider handle sprite - the only circular built-in - so
            // the swatch matches the shape of the sphere it stands for
            var swatchGo = new GameObject($"Key Swatch ({style.Label})", typeof(RectTransform), typeof(Image));
            swatchGo.transform.SetParent(parent, false);
            var swatchRect = (RectTransform)swatchGo.transform;
            swatchRect.anchorMin = swatchRect.anchorMax = swatchRect.pivot = new Vector2(0.5f, 0.5f);
            swatchRect.anchoredPosition = new Vector2(-172f, y);
            swatchRect.sizeDelta = new Vector2(18f, 18f);
            var swatch = swatchGo.GetComponent<Image>();
            swatch.sprite = GetBuiltinSprite("Knob");
            swatch.color = style.Color;
            swatch.raycastTarget = false;

            var label = CreateLabel(parent, $"Key Label ({style.Label})", style.Label,
                new Vector2(0f, y), new Vector2(300f, 24f), 18f, TextAlignmentOptions.Left);
            label.color = k_TextPrimary;
        }
    }

    static Button CreateButton(Transform parent, string label, Vector2 anchoredPosition, Vector2 sizeDelta,
        float fontSize = 20f, bool centerAnchored = false, Color? tint = null)
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

        var image = go.GetComponent<Image>();
        image.sprite = GetBuiltinSprite("UISprite");
        image.type = Image.Type.Sliced;
        // White, because Selectable's colour tint multiplies the CanvasRenderer colour into
        // this one - the actual colour lives in the ColorBlock below so that hover and press
        // states derive from it
        image.color = Color.white;

        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        Color baseColor = tint ?? k_ButtonNeutral;
        var colors = button.colors;
        colors.normalColor = baseColor;
        colors.highlightedColor = Shade(baseColor, 0.22f);
        colors.pressedColor = Shade(baseColor, -0.18f);
        colors.selectedColor = Shade(baseColor, 0.12f);
        // Empty rows in the topic browser are left in place but non-interactable; fading rather
        // than hiding them keeps the page from reflowing under the ray
        colors.disabledColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.3f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        var textGo = new GameObject("Label", typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = fontSize;
        text.color = k_TextPrimary;
        text.raycastTarget = false;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;

        return button;
    }

    static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, Vector2 anchoredPosition,
        Vector2 sizeDelta, float fontSize, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var go = new GameObject(name, typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = k_TextPrimary;
        // A label is never a control, and a raycast-blocking one over a button swallows the
        // press - the ray hits the topmost graphic, not the topmost Selectable
        label.raycastTarget = false;
        var rt = label.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = sizeDelta;
        return label;
    }

    // ---------------------------------------------------------------------------------------
    // Placement handles
    // ---------------------------------------------------------------------------------------

    // A grabbable sphere that repositions something else. One helper for all of them now that
    // PlacementHandle is generic - pass articulationBody for a robot (its root ignores writes
    // to its Transform and has to be teleported), target for everything else.
    static GameObject CreatePlacementHandle(HandleStyle style, Vector3 position, float size,
        Transform target, ArticulationBody articulationBody, Vector3 offset, bool yawOnly = true,
        Quaternion? rotation = null)
    {
        var handle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        handle.name = style.ObjectName;
        handle.transform.localScale = Vector3.one * size;
        // The handle must start at the target's own rotation whenever yawOnly is off, or the
        // first grab snaps the target to identity and throws away however it was aimed
        handle.transform.SetPositionAndRotation(position, rotation ?? Quaternion.identity);

        var material = GetHandleMaterial(style);
        if (material != null)
            handle.GetComponent<MeshRenderer>().sharedMaterial = material;

        var placement = handle.AddComponent<PlacementHandle>();
        var so = new SerializedObject(placement);
        so.FindProperty("_target").objectReferenceValue = target;
        so.FindProperty("_targetArticulationBody").objectReferenceValue = articulationBody;
        so.FindProperty("_offset").vector3Value = offset;
        so.FindProperty("_yawOnly").boolValue = yawOnly;
        so.ApplyModifiedPropertiesWithoutUndo();

        AddGrabInteraction(handle, handle.transform);
        return handle;
    }

    // Makes a visualisation placeable from /tf as an alternative to its grab handle. The anchor
    // goes on the visualisation and drives the handle's target through the handle itself, so the
    // ArticulationBody case (a robot root, which ignores Transform writes) stays in one place.
    // Starts switched off - the scene behaves exactly as before until the panel says otherwise.
    static void AddTfAnchor(GameObject target, GameObject handle, MonoBehaviour frameSource,
        string frameId, string label)
    {
        var anchor = target.AddComponent<TfAnchor>();
        var so = new SerializedObject(anchor);
        so.FindProperty("_handle").objectReferenceValue = handle.GetComponent<PlacementHandle>();
        so.FindProperty("_frameSource").objectReferenceValue = frameSource;
        so.FindProperty("_frameId").stringValue = frameId;
        so.FindProperty("_label").stringValue = label;
        so.FindProperty("_anchorToTf").boolValue = false;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Makes a top-level visualisation (or the robot) show/hide-able from the Visibility panel.
    // Target and marker live on the same GameObject, so hiding it also pauses whatever's
    // subscribing and anchoring on it - it just stops running like any other disabled object.
    static void AddVisibilityTarget(GameObject target, string label)
    {
        var visibility = target.AddComponent<VisibilityTarget>();
        var so = new SerializedObject(visibility);
        so.FindProperty("_label").stringValue = label;
        so.FindProperty("_target").objectReferenceValue = target;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static RosSubscriberImage CreateImageSubscriber(Transform parent, string name, string topic)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sub = go.AddComponent<RosSubscriberImage>();
        var so = new SerializedObject(sub);
        so.FindProperty("_topic").stringValue = topic;
        so.ApplyModifiedPropertiesWithoutUndo();
        return sub;
    }

    // One material asset per handle colour, created on first run and rewritten on every later
    // one so a palette change here propagates. It has to be an asset: a `new Material(...)` from
    // an editor script isn't saved anywhere the player build can find, so the handles would come
    // out untinted (or pink) in the APK.
    static Material GetHandleMaterial(HandleStyle style)
    {
        return GetGeneratedMaterial(style.ObjectName.Replace(" ", string.Empty), style.Color);
    }

    static Material GetGeneratedMaterial(string assetName, Color color)
    {
        var shader = Shader.Find(k_HandleShaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[XRViz] Shader '{k_HandleShaderName}' not found - handles will use the " +
                "default material and all look alike. Check Assets/XRViz/Shaders/HandleUnlit.shader imported.");
            return null;
        }

        if (!AssetDatabase.IsValidFolder(k_MaterialFolder))
            AssetDatabase.CreateFolder("Assets/XRViz", "Materials");

        string path = $"{k_MaterialFolder}/{assetName}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.shader = shader;
        material.SetColor("_Color", color);
        // A near-white rim rather than the handle's own colour, so the silhouette stays crisp
        // against a busy passthrough background without washing the hue out
        material.SetColor("_RimColor", Color.Lerp(color, Color.white, 0.75f));
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssetIfDirty(material);
        return material;
    }

    // A visible marker for the fixed frame, because the TF origin is otherwise an empty
    // GameObject and its grab handle sits a metre above it - "align the origin with the real
    // robot's base" is not an instruction you can follow while looking at nothing.
    //
    // Coloured by ROS convention (x red, y green, z blue) and drawn along the ROS axes, not
    // Unity's: ROS +x forward is Unity +z, ROS +y left is Unity -x, ROS +z up is Unity +y. So the
    // red bar points where the robot's x does, which is what makes this readable next to RViz.
    static void CreateAxisTriad(Transform parent, float length, float thickness)
    {
        (string Name, Vector3 UnityDirection, Color Color)[] axes =
        {
            ("X (ROS forward)", Vector3.forward, new Color(0.95f, 0.26f, 0.21f)),
            ("Y (ROS left)", Vector3.left, new Color(0.30f, 0.85f, 0.39f)),
            ("Z (ROS up)", Vector3.up, new Color(0.26f, 0.52f, 0.96f)),
        };

        foreach (var axis in axes)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = $"TF Axis {axis.Name}";
            bar.transform.SetParent(parent, false);
            // The cube's local +z becomes the bar's length, so this aims it. FromToRotation rather
            // than LookRotation: the up axis is degenerate for LookRotation's default up vector.
            bar.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, axis.UnityDirection);
            bar.transform.localPosition = axis.UnityDirection * (length * 0.5f);
            bar.transform.localScale = new Vector3(thickness, thickness, length);

            // Marker, not a control: a collider here would sit in front of the origin and eat ray
            // hits meant for the handle behind it
            Object.DestroyImmediate(bar.GetComponent<Collider>());

            var material = GetGeneratedMaterial($"TFAxis{axis.Name[0]}", axis.Color);
            if (material != null)
                bar.GetComponent<MeshRenderer>().sharedMaterial = material;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Popup panels
    // ---------------------------------------------------------------------------------------

    // Popup topic browser: asks the endpoint what it's advertising and lets one be picked to
    // subscribe to, from inside the headset. Its own root Canvas, sibling to the status panel
    // (see the note at the panel group on why these must not nest), sitting to its left.
    // Paged rather than scrolled - a ScrollRect is fiddly to hit with a ray, and fixed rows
    // need no viewport mask, layout group or content size fitter.
    static TopicBrowserUI CreateTopicBrowser(Transform parent, MonoBehaviour[] targets)
    {
        const int rowCount = 8;

        var browserGo = new GameObject("Topic Browser Panel", typeof(Canvas));
        browserGo.transform.SetParent(parent, false);
        var browserCanvas = browserGo.GetComponent<Canvas>();
        browserCanvas.renderMode = RenderMode.WorldSpace;
        var browserRect = browserGo.GetComponent<RectTransform>();
        browserRect.sizeDelta = new Vector2(420f, 640f);
        // To the left of the 420-unit-wide status panel, with a gap (the keypad is right)
        browserGo.transform.localPosition = new Vector3(-445f * k_PanelUnitsToMeters, 0f, 0f);
        browserGo.transform.localRotation = Quaternion.identity;
        browserGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
        var title = StylePanel(browserGo, "ROS Topics");

        var targetLabel = CreateLabel(browserGo.transform, "Target Label", "", new Vector2(0f, 228f), new Vector2(270f, 52f), 19f);
        var status = CreateLabel(browserGo.transform, "Status", "", new Vector2(0f, 186f), new Vector2(400f, 26f), 17f);
        status.color = k_TextMuted;
        var pageLabel = CreateLabel(browserGo.transform, "Page Label", "1 / 1", new Vector2(0f, -214f), new Vector2(120f, 44f), 18f);
        pageLabel.color = k_TextMuted;

        var browserUi = browserGo.AddComponent<TopicBrowserUI>();

        // Which subscriber you're retargeting. Sits above the list because it decides what the
        // list contains - the topics are filtered to the selected target's message type.
        var prevTargetButton = CreateButton(browserGo.transform, "◀", new Vector2(-178f, 228f), new Vector2(50f, 52f), 20f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(prevTargetButton.onClick, browserUi.PreviousTarget);
        var nextTargetButton = CreateButton(browserGo.transform, "▶", new Vector2(178f, 228f), new Vector2(50f, 52f), 20f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(nextTargetButton.onClick, browserUi.NextTarget);

        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(browserGo.transform, "", new Vector2(0f, 148f - i * 44f), new Vector2(390f, 40f), 17f, true, Shade(k_ButtonNeutral, -0.25f));
            rows[i].gameObject.name = $"Topic Row {i}";
            // Topic names read better left-aligned, and they're long enough to want the room
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(12f, 0f);
        }

        var prevButton = CreateButton(browserGo.transform, "◀ Prev", new Vector2(-130f, -214f), new Vector2(120f, 44f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(prevButton.onClick, browserUi.PreviousPage);
        var nextButton = CreateButton(browserGo.transform, "Next ▶", new Vector2(130f, -214f), new Vector2(120f, 44f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(nextButton.onClick, browserUi.NextPage);

        var refreshButton = CreateButton(browserGo.transform, "Refresh", new Vector2(-85f, -266f), new Vector2(150f, 44f), 18f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(refreshButton.onClick, browserUi.Refresh);
        var closeButton = CreateButton(browserGo.transform, "Close", new Vector2(85f, -266f), new Vector2(150f, 44f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(closeButton.onClick, browserUi.ToggleVisibility);

        var browserSo = new SerializedObject(browserUi);
        var targetsProp = browserSo.FindProperty("_targets");
        targetsProp.arraySize = targets.Length;
        for (int i = 0; i < targets.Length; i++)
            targetsProp.GetArrayElementAtIndex(i).objectReferenceValue = targets[i];
        browserSo.FindProperty("_title").objectReferenceValue = title;
        browserSo.FindProperty("_targetLabel").objectReferenceValue = targetLabel;
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

    // Popup list of every TF anchor: which frame each visualisation would be placed by, whether
    // that frame has actually turned up in /tf, and a press per row to flip just that one.
    //
    // Sits below the topic browser on the left. The keypad owns the right and the browser the
    // left at eye level; dropping this one under the browser keeps all three visible at once
    // without overlap, and it stays clear of the panel's own grab handle hanging below the middle.
    static TfAnchorPanelUI CreateTfAnchorPanel(Transform parent)
    {
        const int rowCount = 6;
        const float panelHeight = 460f;

        var panelGo = new GameObject("TF Anchors Panel", typeof(Canvas));
        panelGo.transform.SetParent(parent, false);
        var canvas = panelGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = panelGo.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(420f, panelHeight);
        // Below the 640-tall topic browser, in the same left-hand column, with a gap between them
        float dropBelow = 640f * 0.5f + 20f + panelHeight * 0.5f;
        panelGo.transform.localPosition = new Vector3(
            -445f * k_PanelUnitsToMeters, -dropBelow * k_PanelUnitsToMeters, 0f);
        panelGo.transform.localRotation = Quaternion.identity;
        panelGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
        StylePanel(panelGo, "TF Anchors");

        var status = CreateLabel(panelGo.transform, "Status", "", new Vector2(0f, 150f), new Vector2(384f, 52f), 18f);
        status.color = k_TextMuted;

        var panelUi = panelGo.AddComponent<TfAnchorPanelUI>();

        // Two lines per row (name + frame, then state), so the rows are taller than the topic
        // browser's - the frame name is the whole point of this panel and truncating it would
        // defeat it
        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(panelGo.transform, "", new Vector2(0f, 92f - i * 56f),
                new Vector2(390f, 52f), 17f, true, Shade(k_ButtonNeutral, -0.25f));
            rows[i].gameObject.name = $"Anchor Row {i}";
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(12f, 0f);
        }

        var allTfButton = CreateButton(panelGo.transform, "All TF", new Vector2(-130f, -178f), new Vector2(120f, 44f), 18f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(allTfButton.onClick, panelUi.AllTf);
        var allManualButton = CreateButton(panelGo.transform, "All Manual", new Vector2(0f, -178f), new Vector2(120f, 44f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(allManualButton.onClick, panelUi.AllManual);
        var closeButton = CreateButton(panelGo.transform, "Close", new Vector2(130f, -178f), new Vector2(120f, 44f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(closeButton.onClick, panelUi.ToggleVisibility);

        var so = new SerializedObject(panelUi);
        so.FindProperty("_status").objectReferenceValue = status;
        var rowsProp = so.FindProperty("_rows");
        rowsProp.arraySize = rowCount;
        for (int i = 0; i < rowCount; i++)
            rowsProp.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        panelGo.SetActive(false);
        return panelUi;
    }

    // Popup list of every VisibilityTarget: robot, laser scan, point cloud, anything added later.
    // One press per row shows or hides just that one; Show All / Hide All for the rest.
    //
    // Sits below the IP keypad on the right, mirroring the TF Anchors panel's spot under the
    // topic browser on the left - keeps the panel group's floating popups in two predictable
    // columns instead of scattered around the status panel.
    static VisibilityPanelUI CreateVisibilityPanel(Transform parent)
    {
        const int rowCount = 6;
        const float panelHeight = 420f;

        var panelGo = new GameObject("Visibility Panel", typeof(Canvas));
        panelGo.transform.SetParent(parent, false);
        var canvas = panelGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = panelGo.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(420f, panelHeight);
        // Below the 440-tall IP keypad, in the same right-hand column, with a gap between them
        float dropBelow = 440f * 0.5f + 20f + panelHeight * 0.5f;
        panelGo.transform.localPosition = new Vector3(
            445f * k_PanelUnitsToMeters, -dropBelow * k_PanelUnitsToMeters, 0f);
        panelGo.transform.localRotation = Quaternion.identity;
        panelGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
        StylePanel(panelGo, "Visibility");

        var status = CreateLabel(panelGo.transform, "Status", "", new Vector2(0f, 150f), new Vector2(384f, 30f), 17f);
        status.color = k_TextMuted;

        var panelUi = panelGo.AddComponent<VisibilityPanelUI>();

        // One line per row (label + shown/hidden), unlike the TF panel's two - there's no frame
        // name to make room for here
        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(panelGo.transform, "", new Vector2(0f, 110f - i * 40f),
                new Vector2(390f, 38f), 17f, true, Shade(k_ButtonNeutral, -0.25f));
            rows[i].gameObject.name = $"Visibility Row {i}";
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(12f, 0f);
        }

        var showAllButton = CreateButton(panelGo.transform, "Show All", new Vector2(-130f, -166f), new Vector2(120f, 44f), 18f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(showAllButton.onClick, panelUi.ShowAll);
        var hideAllButton = CreateButton(panelGo.transform, "Hide All", new Vector2(0f, -166f), new Vector2(120f, 44f), 18f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(hideAllButton.onClick, panelUi.HideAll);
        var closeButton = CreateButton(panelGo.transform, "Close", new Vector2(130f, -166f), new Vector2(120f, 44f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(closeButton.onClick, panelUi.ToggleVisibility);

        var so = new SerializedObject(panelUi);
        so.FindProperty("_status").objectReferenceValue = status;
        var rowsProp = so.FindProperty("_rows");
        rowsProp.arraySize = rowCount;
        for (int i = 0; i < rowCount; i++)
            rowsProp.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        panelGo.SetActive(false);
        return panelUi;
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
        keypadRect.sizeDelta = new Vector2(320f, 440f);
        // Sits to the right of the 420-unit-wide status panel with a gap between them
        keypadGo.transform.localPosition = new Vector3(400f * k_PanelUnitsToMeters, 0f, 0f);
        keypadGo.transform.localRotation = Quaternion.identity;
        keypadGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;
        StylePanel(keypadGo, "ROS IP");

        var displayGo = new GameObject("Display", typeof(TextMeshProUGUI));
        displayGo.transform.SetParent(keypadGo.transform, false);
        var display = displayGo.GetComponent<TextMeshProUGUI>();
        display.fontSize = 28f;
        display.color = k_TextPrimary;
        display.raycastTarget = false;
        display.alignment = TextAlignmentOptions.Center;
        var displayRect = display.rectTransform;
        displayRect.anchorMin = displayRect.anchorMax = displayRect.pivot = new Vector2(0.5f, 0.5f);
        displayRect.anchoredPosition = new Vector2(0f, 128f);
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
            float y = 80f - row * 58f;
            for (int col = 0; col < 3; col++)
            {
                float x = -100f + col * 100f;
                string key = grid[row, col];
                bool isBackspace = key == "back";
                var keyButton = CreateButton(keypadGo.transform, isBackspace ? "⌫" : key, new Vector2(x, y), new Vector2(90f, 50f), 22f, true, k_ButtonNeutral);
                if (isBackspace)
                    UnityEventTools.AddVoidPersistentListener(keyButton.onClick, keypadUi.Backspace);
                else
                    UnityEventTools.AddStringPersistentListener(keyButton.onClick, keypadUi.AppendChar, key);
            }
        }

        var clearButton = CreateButton(keypadGo.transform, "Clear", new Vector2(-80f, -160f), new Vector2(140f, 50f), 18f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(clearButton.onClick, keypadUi.Clear);

        var applyButton = CreateButton(keypadGo.transform, "Apply", new Vector2(80f, -160f), new Vector2(140f, 50f), 18f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(applyButton.onClick, keypadUi.Apply);

        keypadGo.SetActive(false);
        return keypadUi;
    }

    // ---------------------------------------------------------------------------------------
    // Interaction SDK wiring
    // ---------------------------------------------------------------------------------------

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
