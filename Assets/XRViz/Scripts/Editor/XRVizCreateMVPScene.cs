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
        "RGBd Camera", "RGBd Camera Placement Handle",
        // legacy: both were named "Point Cloud ..." before the RGBD path was renamed. Kept so
        // regenerating over an older scene still clears the objects it made under the old names.
        "Point Cloud", "Point Cloud Placement Handle",
        "PointCloud2", "PointCloud2 Placement Handle",
        "Camera Image", "Camera Image Placement Handle",
        "TF Origin", "TF Origin Placement Handle",
        "ArUco Calibration", "ROS Diagnostics",
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
        ObjectName = "RGBd Camera Placement Handle",
        Label = "RGBd Camera origin",
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
    // Violet: the sixth hue that stays apart from the other five for the common colour
    // deficiencies. It sits next to the green RGBD cloud in the key on purpose - they are both
    // point clouds, and which one you are looking at is a question you will have.
    static readonly HandleStyle k_PointCloud2HandleStyle = new HandleStyle
    {
        ObjectName = "PointCloud2 Placement Handle",
        Label = "PointCloud2 origin",
        Color = new Color(0.55f, 0.45f, 1f),
    };
    // Yellow. Seven distinct hues is the practical ceiling for handles read through
    // passthrough, and this is the last one that stays apart from the rest - it is warmer and
    // much brighter than the robot's amber, which is the only one it is close to in hue.
    static readonly HandleStyle k_ImageWindowHandleStyle = new HandleStyle
    {
        ObjectName = "Camera Image Placement Handle",
        Label = "Camera image window",
        Color = new Color(1f, 0.87f, 0.25f),
    };
    static readonly HandleStyle[] k_HandleStyles =
    {
        k_RobotHandleStyle, k_PanelHandleStyle, k_ScanHandleStyle, k_PointCloudHandleStyle,
        k_PointCloud2HandleStyle, k_ImageWindowHandleStyle, k_TfOriginHandleStyle,
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

    // --- Panel geometry ------------------------------------------------------------------
    //
    // ONE panel, 0.62 x 0.66 m: a rail of tabs down the left, one page at a time on the right,
    // a header that always shows the connection state and a footer that always shows what the
    // last press did. It replaces a 420 x 752 status panel plus five popups that floated around
    // it across about two metres of room.
    //
    // All of these are canvas units measured from the panel's centre, so each number reads
    // directly as "this far from the middle".
    static readonly Vector2 k_PanelSize = new Vector2(620f, 660f);

    // Tab rail
    const float k_RailX = -230f;
    const float k_TabTopY = 216f;
    const float k_TabStep = 62f;
    const float k_RailDividerX = -148f;
    static readonly Vector2 k_TabSize = new Vector2(150f, 54f);

    // The content area every page is laid out inside, from its own centre
    static readonly Vector2 k_PageSize = new Vector2(420f, 520f);
    static readonly Vector2 k_PageCentre = new Vector2(86f, -10f);

    static readonly Vector2 k_ButtonSize = new Vector2(194f, 54f);

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
        // Its handle sits ON it, unlike every other handle in the scene. This one's whole job is
        // lining the fixed frame up with the real room, and a sphere floating a metre up and
        // diagonally across the trolley cannot be lined up against anything - it also reads as
        // belonging to some other object entirely. The robot's handle is the one out on the
        // trolley corner, so the two are still never in the same place.
        var tfOriginGo = new GameObject("TF Origin");
        tfOriginGo.transform.SetPositionAndRotation(k_RobotBasePosition, Quaternion.identity);
        tfOriginGo.AddComponent<RosTfTree>();
        CreateAxisTriad(tfOriginGo.transform, length: 0.15f, thickness: 0.008f);
        // The bars say where the fixed frame is; this says what it IS - and whether /tf is
        // arriving into it, how many visualisations are placed from it, and when something
        // (the ArUco calibration, most importantly) has just moved it.
        var tfIndicator = tfOriginGo.AddComponent<TfOriginIndicator>();
        var tfIndicatorSo = new SerializedObject(tfIndicator);
        // Not permanent: B on the right controller brings the card up when the question comes up
        tfIndicatorSo.FindProperty("_showLabel").boolValue = false;
        tfIndicatorSo.FindProperty("_toggleButton").intValue = (int)OVRInput.Button.Two;
        tfIndicatorSo.FindProperty("_toggleController").intValue = (int)OVRInput.Controller.RTouch;
        tfIndicatorSo.ApplyModifiedPropertiesWithoutUndo();

        CreatePlacementHandle(k_TfOriginHandleStyle, tfOriginGo.transform.position,
            0.05f, target: tfOriginGo.transform, articulationBody: null, offset: Vector3.zero);

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
        var cloudGo = new GameObject("RGBd Camera");
        cloudGo.transform.SetPositionAndRotation(cloudPosition, cloudRotation);

        // Raw sensor_msgs/Image on child objects rather than three components stacked on the
        // cloud root: two of them are the same type, and in the Inspector (and in the topic
        // browser's target list, which labels every binding by its GameObject name) the name is
        // the only thing that tells them apart at a glance. All three carry the "RGBd Camera"
        // prefix so the browser groups them under the visualisation they feed, and so they cannot
        // be confused with PointCloud2's single binding or the Camera Image window's.
        // No default topic - see the note on the laser scan above.
        var colorSub = CreateImageSubscriber(cloudGo.transform, "RGBd Camera Color", "");
        var depthSub = CreateImageSubscriber(cloudGo.transform, "RGBd Camera Depth", "");

        var infoGo = new GameObject("RGBd Camera Info");
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
            Debug.LogWarning($"[XRViz] RGBd Camera compute shader not found at {k_PointCloudComputePath}; " +
                "assign it on the RGBd Camera object by hand or the cloud will disable itself.");
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
        AddTfAnchor(cloudGo, cloudHandle, depthSub, frameId: string.Empty, label: "RGBd Camera");
        AddVisibilityTarget(cloudGo, "RGBd Camera");

        // Raw sensor_msgs/PointCloud2, drawn as a mesh of camera-facing squares. Separate from
        // the RGBD cloud above rather than a mode of it, because the two are genuinely different
        // pipelines: that one reconstructs on the GPU from depth + CameraInfo and moves far less
        // over the socket, this one draws whatever a /points topic actually contains. A lidar, or
        // a node that has already fused something, only ever offers the latter.
        //
        // Placed to the robot's left so it doesn't start inside the RGBD cloud, and levelled the
        // same way (yaw only) so its floor stays parallel to the room's.
        var cloud2Position = new Vector3(-0.9f, 1.0f, 0.5f);
        var cloud2Go = new GameObject("PointCloud2", typeof(MeshFilter), typeof(MeshRenderer));
        cloud2Go.transform.SetPositionAndRotation(cloud2Position, Quaternion.identity);

        var cloud2Sub = cloud2Go.AddComponent<RosSubscriberPointCloud2>();
        var cloud2SubSo = new SerializedObject(cloud2Sub);
        // No default topic, like the scan and the RGBD cloud - Subscribe() no-ops on an empty
        // string, so this stays unsubscribed until a topic is picked from the Topics tab
        cloud2SubSo.FindProperty("_topic").stringValue = "";
        cloud2SubSo.ApplyModifiedPropertiesWithoutUndo();

        var cloud2Vis = cloud2Go.AddComponent<PointCloud2Visualizer>();
        var cloud2VisSo = new SerializedObject(cloud2Vis);
        cloud2VisSo.FindProperty("_cloudSub").objectReferenceValue = cloud2Sub;
        cloud2VisSo.ApplyModifiedPropertiesWithoutUndo();

        // Handle below the cloud's origin, like the scan's. Unlike the RGBD cloud this origin is
        // the sensor frame rather than an optical centre to be aimed, so yaw-only is right.
        var cloud2Handle = CreatePlacementHandle(k_PointCloud2HandleStyle,
            cloud2Position + new Vector3(0f, -0.15f, 0f), 0.03f,
            target: cloud2Go.transform, articulationBody: null,
            offset: new Vector3(0f, 0.15f, 0f));

        // Frame comes from the cloud's own header, so retargeting the topic retargets the anchor.
        // TfAnchor applies the optical-frame correction automatically on a *_optical_frame, which
        // is what an RGBD /points topic publishes in - and RosSubscriberPointCloud2 decides the
        // same way, so the points and the transform placing them always agree.
        AddTfAnchor(cloud2Go, cloud2Handle, cloud2Sub, frameId: string.Empty, label: "PointCloud2");
        AddVisibilityTarget(cloud2Go, "PointCloud2");

        // Floating camera image window. Not TF-anchored and deliberately so: an image is a
        // picture rather than geometry, and there is no pose at which it is "correct" - it goes
        // where you want to look at it. Hence a handle and no TfAnchor.
        //
        // Placed to the robot's right at eye height, facing the same way the control panel does.
        var imagePosition = new Vector3(-0.75f, 1.35f, 1.1f);
        var imageGo = new GameObject("Camera Image", typeof(MeshFilter), typeof(MeshRenderer));
        imageGo.transform.SetPositionAndRotation(imagePosition,
            Quaternion.LookRotation(imagePosition - new Vector3(0f, 1.5f, 0f)));

        var imageSub = CreateImageSubscriber(imageGo.transform, "Camera Image Source", "");

        var imageWindow = imageGo.AddComponent<ImageWindow>();
        var imageWindowSo = new SerializedObject(imageWindow);
        imageWindowSo.FindProperty("_imageSub").objectReferenceValue = imageSub;
        imageWindowSo.ApplyModifiedPropertiesWithoutUndo();

        // Yaw only, which is the whole point: the window follows the handle around the room and
        // turns to face you, but stays upright. A picture tipped out of vertical is unreadable,
        // and unlike a sensor there is no reason ever to aim one.
        // A starting offset only. ImageWindow recomputes it at Awake and on every shape change,
        // because the window's height in metres - and so where its bottom edge is - is not known
        // until a frame arrives and its aspect is read.
        var imageHandleOffset = new Vector3(0f, -0.3f, 0f);
        var imageHandle = CreatePlacementHandle(k_ImageWindowHandleStyle, imagePosition + imageHandleOffset, 0.035f,
            target: imageGo.transform, articulationBody: null, offset: -imageHandleOffset,
            yawOnly: true);

        AddVisibilityTarget(imageGo, "Camera Image");

        // World-space ROS control panel: ONE Canvas, a rail of tabs down the left, one page
        // showing at a time.
        //
        // Pages are plain RectTransforms under this Canvas and must never be nested Canvases.
        // PointableCanvasModule.FindFirstRaycastWithinCanvas discards any raycast hit whose
        // Canvas.rootCanvas isn't the exact Canvas injected into the PointableCanvas, and a
        // nested Canvas reports its outermost ancestor as its rootCanvas - so a nested page's
        // buttons silently stop receiving hits. The previous layout dodged that by making each
        // popup a SIBLING root Canvas, which worked, but put six panels in a two-metre cross
        // around the user (the calibration one 0.9 m off to the right, a head turn away from the
        // robot you are calibrating against), let several be open at once, and needed a
        // ray-interaction rig per Canvas. One Canvas with plain pages needs one rig and keeps
        // every control in the same place.
        var panelPosition = new Vector3(0.5f, 1.3f, 0.9f);
        var panelRootGo = new GameObject("ROS Control Panel");
        panelRootGo.transform.SetPositionAndRotation(
            panelPosition, Quaternion.LookRotation(panelPosition - new Vector3(0f, 1.5f, 0f)));

        // Both of these live on the group root, which stays active while the panel is hidden:
        // the menu toggle has to keep polling for the Menu button, and the feedback line has to
        // keep expiring no matter which page is open.
        var menuToggle = panelRootGo.AddComponent<ControlPanelMenuToggle>();

        // Y (left controller) hides and shows the camera image window. It goes here rather than
        // on the window because VisibilityTarget hides by deactivating, and a deactivated object
        // stops polling for the button that would bring it back. Same reason the Menu toggle is
        // on this root.
        var imageHotkey = panelRootGo.AddComponent<VisibilityHotkey>();
        var imageHotkeySo = new SerializedObject(imageHotkey);
        imageHotkeySo.FindProperty("_targetLabel").stringValue = "Camera Image";
        imageHotkeySo.FindProperty("_button").intValue = (int)OVRInput.Button.Two;
        imageHotkeySo.FindProperty("_controller").intValue = (int)OVRInput.Controller.LTouch;
        imageHotkeySo.ApplyModifiedPropertiesWithoutUndo();
        var actions = panelRootGo.AddComponent<ControlPanelActions>();

        // Makes and unmakes copies of the visualisations at runtime. On the panel's root rather
        // than on the Views page: a page only exists while it is the one showing, and the copies
        // it has made have to outlive navigating away from it.
        var spawner = panelRootGo.AddComponent<VisualizationSpawner>();
        ConfigureSpawner(spawner, new (string Label, GameObject Template, GameObject Handle, float Spacing)[]
        {
            // Sensors get a wide step - two clouds half a metre apart read as two clouds, two
            // 20 cm apart as one confusing one. Image windows are flat panels, so they tile.
            ("Laser Scan", scanGo, scanHandle, 0.7f),
            ("RGBd Camera", cloudGo, cloudHandle, 0.7f),
            ("PointCloud2", cloud2Go, cloud2Handle, 0.7f),
            ("Camera Image", imageGo, imageHandle, 0.45f),
        });

        var canvasGo = new GameObject("ROS Panel", typeof(Canvas));
        canvasGo.transform.SetParent(panelRootGo.transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta = k_PanelSize;
        canvasGo.transform.localScale = Vector3.one * k_PanelUnitsToMeters;

        // Title left, live connection state right. The connection is the one piece of state
        // worth seeing from every page, and a centred title leaves nowhere to put it.
        var title = StylePanel(canvasGo, "XRViz");
        title.alignment = TextAlignmentOptions.Left;
        title.rectTransform.offsetMin = new Vector2(18f, 0f);
        var headerStatus = CreateHeaderStatus(title.transform.parent);

        // The status component goes on the Canvas rather than on the ROS page, because a hidden
        // page stops updating - and the header connection state must not freeze the moment you
        // open Topics.
        var jointStateSub = robot.GetComponentInChildren<RosSubscriberJointState>();
        var statusUi = canvasGo.AddComponent<RosConnectionStatusUI>();
        var tabs = canvasGo.AddComponent<ControlPanelTabs>();

        // Chassis-tag calibration rig, built before the pages since the Calibrate page drives it
        var calibrator = CreateArucoCalibrationRig(robot, robotHandle);

        // A short word on the tab, the full name in the header once you are there - the rail
        // stays narrow without the tabs becoming cryptic.
        (string Tab, string Title)[] pageNames =
        {
            ("ROS", "XRViz  \u00b7  ROS connection"),
            ("Topics", "XRViz  \u00b7  Topics"),
            // Next to Topics on purpose: adding a view and giving it a topic are one task
            // done in two steps, and they are the two tabs you move between while doing it.
            ("Views", "XRViz  \u00b7  How many of each"),
            ("TF", "XRViz  \u00b7  TF placement"),
            ("Scene", "XRViz  \u00b7  Scene"),
            ("Calibrate", "XRViz  \u00b7  Chassis calibration"),
            ("Guide", "XRViz  \u00b7  Guide"),
            // The keypad has no tab: it is a step inside changing the IP, not a place you visit
            (null, "XRViz  \u00b7  ROS IP address"),
        };
        const int keypadPageIndex = 7;

        var pageRoots = new GameObject[pageNames.Length];
        for (int i = 0; i < pageNames.Length; i++)
            pageRoots[i] = CreatePage(canvasGo.transform, (pageNames[i].Tab ?? "Keypad") + " Page");

        CreateKeypadPage(pageRoots[keypadPageIndex].transform, statusUi, tabs);
        var rosStatusText = CreateRosPage(pageRoots[0].transform, statusUi, tabs, keypadPageIndex);
        // No target list: the browser finds every subscriber in the scene, which is the only way
        // the copies made on the Views page can turn up in it
        CreateTopicsPage(pageRoots[1].transform);
        CreateViewsPage(pageRoots[2].transform, spawner, actions);
        CreateFramesPage(pageRoots[3].transform, actions);
        CreateScenePage(pageRoots[4].transform, actions);
        CreateCalibratePage(pageRoots[5].transform, calibrator);
        CreateGuidePage(pageRoots[6].transform);

        var statusSo = new SerializedObject(statusUi);
        statusSo.FindProperty("_jointStateSub").objectReferenceValue = jointStateSub;
        statusSo.FindProperty("_statusText").objectReferenceValue = rosStatusText;
        statusSo.FindProperty("_headerStatus").objectReferenceValue = headerStatus;
        statusSo.ApplyModifiedPropertiesWithoutUndo();

        // The rail, and the rule that separates it from the page
        CreateVerticalDivider(canvasGo.transform, k_RailDividerX, top: 250f, bottom: -284f);

        var tabButtons = new Button[pageNames.Length - 1]; // every page but the keypad
        for (int i = 0; i < tabButtons.Length; i++)
        {
            tabButtons[i] = CreateButton(canvasGo.transform, pageNames[i].Tab,
                new Vector2(k_RailX, k_TabTopY - i * k_TabStep), k_TabSize, 21f, true, k_ButtonNeutral);
            UnityEventTools.AddIntPersistentListener(tabButtons[i].onClick, tabs.ShowPage, i);
        }

        // Dismissing the panel from the panel. The Menu button does it too, but that is a
        // controller button - a hand-tracking user hasn't got one, and putting the panel away
        // shouldn't need reaching for hardware.
        var hideButton = CreateButton(canvasGo.transform, "Hide", new Vector2(k_RailX, -250f),
            new Vector2(k_TabSize.x, 46f), 19f, true, Shade(k_ButtonNeutral, -0.25f));
        UnityEventTools.AddVoidPersistentListener(hideButton.onClick, menuToggle.Hide);

        // Footer: what the last press did, in one fixed place whichever page did it
        CreateDivider(canvasGo.transform, -284f, 560f);
        var feedback = CreateLabel(canvasGo.transform, "Action Feedback", "",
            new Vector2(0f, -306f), new Vector2(572f, 28f), 19f);
        feedback.color = k_TextMuted;

        var actionsSo = new SerializedObject(actions);
        actionsSo.FindProperty("_feedback").objectReferenceValue = feedback;
        actionsSo.ApplyModifiedPropertiesWithoutUndo();

        var tabsSo = new SerializedObject(tabs);
        tabsSo.FindProperty("_title").objectReferenceValue = title;
        tabsSo.FindProperty("_defaultPage").intValue = 0;
        tabsSo.FindProperty("_activeTint").colorValue = k_ButtonAccent;
        tabsSo.FindProperty("_inactiveTint").colorValue = Shade(k_ButtonNeutral, -0.25f);
        var pagesProp = tabsSo.FindProperty("_pages");
        var titlesProp = tabsSo.FindProperty("_pageTitles");
        pagesProp.arraySize = pageRoots.Length;
        titlesProp.arraySize = pageNames.Length;
        for (int i = 0; i < pageRoots.Length; i++)
        {
            pagesProp.GetArrayElementAtIndex(i).objectReferenceValue = pageRoots[i];
            titlesProp.GetArrayElementAtIndex(i).stringValue = pageNames[i].Title;
        }
        var tabsProp = tabsSo.FindProperty("_tabs");
        tabsProp.arraySize = tabButtons.Length;
        for (int i = 0; i < tabButtons.Length; i++)
            tabsProp.GetArrayElementAtIndex(i).objectReferenceValue = tabButtons[i];
        tabsSo.ApplyModifiedPropertiesWithoutUndo();

        // One Canvas, so one ray rig - the old layout needed six, each with its own interactable
        // competing for the same pointer
        AddRayInteractionToCanvas(canvas);

        // Grab handle below the panel; the panel follows, same as the robot handle. The offset
        // clears the panel's own half-height (660 units x 0.001 = 33 cm) plus a small gap.
        CreatePlacementHandle(k_PanelHandleStyle, panelPosition + new Vector3(0f, -0.39f, 0f),
            0.04f, target: panelRootGo.transform, articulationBody: null, offset: new Vector3(0f, 0.39f, 0f));

        // Log-only diagnostics, its own root object so it is obvious in the hierarchy and easy
        // to switch off. It is the only way to see ROS state from a build launched over adb with
        // nobody in the headset: nothing else connects on its own, and the topic list is
        // otherwise only fetched when the Topics tab is opened.
        var diagnosticsGo = new GameObject("ROS Diagnostics");
        diagnosticsGo.AddComponent<RosDiagnosticsReporter>();

        EditorSceneManager.SaveScene(scene, k_ScenePath);
        AddToBuildSettings(k_ScenePath);

        const string nextSteps =
            "MVP scene updated and added to Build Settings. Re-running this only replaces the " +
            "objects it generates (robot, handles, ROS panel, laser scan, point cloud, ArUco " +
            "rig) - Camera Rig, Passthrough, and any other Building Blocks you've added are left " +
            "alone and don't need to be re-added. Inspector tweaks on the objects it DOES " +
            "generate are replaced, so re-do those after running this.\n\n" +

            "THE CONTROL PANEL is one panel with a rail of tabs down its left side, and one page " +
            "showing at a time:\n" +
            "  ROS - connect, disconnect, retype the endpoint IP on a keypad\n" +
            "  Topics - ask the endpoint what it's advertising and re-point a subscriber at a " +
            "different topic; the arrows pick which subscriber, the list is filtered to that " +
            "subscriber's message type, and the red X detaches it from its topic again\n" +
            "  Views - how many of each visualisation the scene has, with a - and a + per kind\n" +
            "  TF - per-visualisation TF/manual placement, with the frame each one is using, " +
            "plus All to TF / All to Manual\n" +
            "  Scene - show/hide each visualisation, Clear Data, Reset Layout\n" +
            "  Calibrate - the chassis-tag calibration (Android only)\n" +
            "  Guide - the handle colour key and the things nobody can guess\n\n" +

            "The header carries the live connection state on every page, and the footer says what " +
            "the last press did. The panel starts hidden; the left controller's Menu button shows " +
            "it, Hide (under the tabs) puts it away, and pressing Menu while facing away from it " +
            "brings it to you rather than leaving it stranded across the room.\n\n" +

            "Every placement handle is a small coloured sphere - amber for the robot, cyan for the " +
            "control panel, magenta for the laser scan, green for the point cloud origin, white (and " +
            "a size up) for the TF origin, with a key on the panel's Guide tab. Each is movable out " +
            "of the box: near grab (reach out and grab it) AND ray grab (point at it from a distance " +
            "and hold the trigger); whatever it's pointed at follows either way.\n\n" +

            "SEVERAL OF THE SAME THING. The Views tab has a row per kind of visualisation - " +
            "laser scan, RGBd camera, PointCloud2, camera image - with a count and a -/+ that " +
            "adds and removes copies at runtime. A copy is a full visualisation: its own " +
            "subscribers, its own grab handle (same colour as the original's), its own TF anchor " +
            "and its own row on the TF and Scene tabs. It arrives beside the one it was copied " +
            "from with NO topic bound, so give it one on the Topics tab, where it appears as its " +
            "own target - that is the whole point, two camera windows on two different cameras. " +
            "The count includes the scene's own instance and stops at 1: to get rid of that one, " +
            "hide it on the Scene tab or detach its topic with the Topics tab's X. The robot has " +
            "no row, deliberately - there is one robot.\n\n" +

            "DETACHING A TOPIC. The red X on the Topics tab unsubscribes the selected subscriber " +
            "from the endpoint: its callback is dropped and nothing more comes over the socket " +
            "for it. It is the same state everything starts in, so re-pick a topic to bring it " +
            "back. Two subscribers may share one topic; detaching one leaves the other running.\n\n" +

            "TF ANCHORING. Press 'All to TF' on the TF tab and the robot, laser scan and point cloud " +
            "stop being placed by hand and are placed from /tf instead - each at its own frame, all " +
            "consistent with each other. Their handles hide while that's on, because TF owns the pose; " +
            "the WHITE TF origin handle stays, and it is now the only thing to align: put its small " +
            "red/green/blue axis triad (ROS x/y/z) on the real robot's base and everything else lands " +
            "where /tf says it is. The label floating above the triad says whether /tf is arriving, " +
            "how many visualisations are placed from it, and when something last moved it. Frames " +
            "come from each topic's own header, so retargeting a topic retargets its anchor - except " +
            "the robot's, which is typed on its TfAnchor component (default base_link).\n\n" +

            "THE CAMERA IMAGE WINDOW (yellow handle) puts any sensor_msgs/Image topic on a " +
            "floating panel. It is not TF-placed - an image is a picture, not geometry - so it " +
            "goes wherever you drag it, and its handle is yaw-only so it stays upright. " +
            "Orientation, bgr8 vs rgb8 and mono8 are all corrected from the encoding the message " +
            "reports, so no camera needs configuring. PRESS Y on the left controller to hide or " +
            "show it - it is the one visualisation big enough to be worth getting out of the " +
            "way, and its grab handle goes with it.\n\n" +

            "TWO POINT CLOUDS, on purpose. 'RGBd Camera' (green handle) is the RGBD path: raw " +
            "color + depth Image plus the depth CameraInfo, reconstructed on the GPU. " +
            "'PointCloud2' (violet handle) subscribes to a sensor_msgs/PointCloud2 topic directly " +
            "and draws it as a mesh, so it can be grabbed and TF-anchored like anything else. " +
            "Use the RGBd Camera path where you have the choice - it moves far less over the socket - " +
            "and PointCloud2 for a lidar or an already-fused /points topic.\n\n" +

            "THE RGBd CAMERA'S HANDLE IS ITS ORIGIN. The camera's optical centre sits on that green " +
            "sphere and the cloud projects out along its +Z, so park the handle where the real " +
            "camera stands in the room and the virtual geometry lands on the real geometry. It is " +
            "the one handle that takes full rotation rather than yaw only, because a camera has to " +
            "be aimed. None of the laser scan or point cloud subscribers start with a default " +
            "topic - pick 'RGBd Camera Color', 'RGBd Camera Depth' and 'RGBd Camera Info' from the Topics tab before " +
            "either visualisation has anything to draw.\n\n" +

            "These only work once the interactor rig exists, via Meta Building Blocks " +
            "(Meta > Tools > Building Blocks) — add each ONCE per project, not per scene:\n" +
            "1. [Camera Rig] and [Passthrough]\n" +
            "2. [Grab Interaction] (near grab on the placement handles)\n" +
            "3. [Ray Interaction] (the panel's buttons, and ray grab on the handles)\n" +
            "4. Optional: [Hand Tracking]\n" +
            "5. Run Meta > Tools > Project Setup Tool and Fix All for Android\n\n" +
            "THE TF ORIGIN CARD names the fixed frame, says whether /tf is arriving and counts " +
            "how many visualisations are actually placed from it. PRESS B on the right controller " +
            "to bring it up - it is a question you ask occasionally, not a panel worth parking " +
            "over the robot. Its white handle sits ON the origin, because aligning that frame " +
            "with the real room is the one thing it is for.\n\n" +

            "DIAGNOSTICS. The 'ROS Diagnostics' object logs, every 5 s, the connection state and " +
            "one line per subscriber saying what it is bound to and what has reached it - no " +
            "topic bound / 0 messages / stale / receiving. It also connects on start (the panel's " +
            "Connect button otherwise being unpressable over adb) and dumps the endpoint's whole " +
            "topic list grouped by type. Read it with `adb logcat -s Unity`, or switch the object " +
            "off for a build you want silent.\n\n" +
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
        var heading = CreateLabel(parent, "Anchor Key Heading", "Grab handles",
            new Vector2(0f, headingY), new Vector2(400f, 26f), 19f, TextAlignmentOptions.Left);
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
            swatchRect.anchoredPosition = new Vector2(-186f, y);
            swatchRect.sizeDelta = new Vector2(18f, 18f);
            var swatch = swatchGo.GetComponent<Image>();
            swatch.sprite = GetBuiltinSprite("Knob");
            swatch.color = style.Color;
            swatch.raycastTarget = false;

            var label = CreateLabel(parent, $"Key Label ({style.Label})", style.Label,
                new Vector2(6f, y), new Vector2(340f, 26f), 18f, TextAlignmentOptions.Left);
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

    // ---------------------------------------------------------------------------------------
    // Panel pages
    // ---------------------------------------------------------------------------------------

    // One page of the control panel: a plain RectTransform filling the content area, NEVER a
    // Canvas of its own (see the note in Run() on rootCanvas and silently lost raycasts).
    // Everything on a page is laid out from the page's own centre, so a page says nothing about
    // where in the panel it sits and pages can be reordered without touching their contents.
    static GameObject CreatePage(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = k_PageCentre;
        rt.sizeDelta = k_PageSize;
        return go;
    }

    // The connection state, right-aligned inside the header bar so it is on screen from every
    // page. RosConnectionStatusUI writes it.
    static TextMeshProUGUI CreateHeaderStatus(Transform header)
    {
        var go = new GameObject("Connection State", typeof(TextMeshProUGUI));
        go.transform.SetParent(header, false);
        var label = go.GetComponent<TextMeshProUGUI>();
        label.fontSize = 20f;
        label.alignment = TextAlignmentOptions.Right;
        label.color = k_TextPrimary;
        label.raycastTarget = false;
        var rt = label.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(12f, 0f);
        rt.offsetMax = new Vector2(-18f, 0f);
        return label;
    }

    // Separates the tab rail from the page. Same reasoning as the horizontal one: at arm's
    // length through passthrough, whitespace alone doesn't convincingly separate two regions.
    static void CreateVerticalDivider(Transform parent, float x, float top, float bottom)
    {
        var go = new GameObject("Rail Divider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, (top + bottom) * 0.5f);
        rt.sizeDelta = new Vector2(2f, top - bottom);
        var image = go.GetComponent<Image>();
        image.color = k_Divider;
        image.raycastTarget = false;
    }

    // The muted paragraph at the foot of a page saying what the page is for, or what its buttons
    // will actually do. There is no tooltip in a headset, no hover, and no manual within reach:
    // if a control needs a sentence, the sentence has to be on the panel next to it.
    static TextMeshProUGUI CreateHint(Transform parent, Vector2 anchoredPosition, Vector2 size,
        string text, float fontSize = 16f)
    {
        var label = CreateLabel(parent, "Hint", text, anchoredPosition, size, fontSize,
            TextAlignmentOptions.TopLeft);
        label.color = k_TextMuted;
        return label;
    }

    // Page: the connection itself. First tab because nothing else on the panel does anything
    // until this one says connected. Returns the status block for RosConnectionStatusUI to write.
    static TextMeshProUGUI CreateRosPage(Transform page, RosConnectionStatusUI statusUi,
        ControlPanelTabs tabs, int keypadPageIndex)
    {
        var status = CreateLabel(page, "Status Text", "ROS status\u2026", new Vector2(0f, 150f),
            new Vector2(400f, 170f), 22f, TextAlignmentOptions.TopLeft);

        CreateDivider(page, 46f, 400f);

        var connectButton = CreateButton(page, "Connect", new Vector2(-102f, 0f),
            k_ButtonSize, 21f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(connectButton.onClick, statusUi.Connect);
        var disconnectButton = CreateButton(page, "Disconnect", new Vector2(102f, 0f),
            k_ButtonSize, 21f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(disconnectButton.onClick, statusUi.Disconnect);

        // Full width because it goes somewhere rather than doing something - the shape is the
        // difference between "this acts now" and "this opens a thing"
        var editIpButton = CreateButton(page, "Edit IP address", new Vector2(0f, -68f),
            new Vector2(400f, 54f), 21f, true, k_ButtonAccent);
        UnityEventTools.AddIntPersistentListener(editIpButton.onClick, tabs.ShowPage, keypadPageIndex);

        CreateHint(page, new Vector2(0f, -178f), new Vector2(400f, 140f),
            "Connect to the ros_tcp_endpoint node on the robot's network. Nothing else in this " +
            "panel can do anything until this reads connected.\n\nThen pick topics on the " +
            "Topics tab - the laser scan and the point cloud start with none.");

        return status;
    }

    // Page: asks the endpoint what it is advertising and re-points a subscriber at a different
    // topic without leaving the headset. The target picker sits above the list because it decides
    // what the list contains - topics are filtered to the selected subscriber's message type.
    //
    // Paged rather than scrolled: a ScrollRect is fiddly to hit with a ray, and fixed rows need
    // no viewport mask, layout group or content size fitter.
    // Page: what each subscriber in the scene is bound to, and what else it could be bound to.
    //
    // No serialized target list. The browser finds every IRosTopicBinding in the scene itself,
    // which is what lets a camera window added on the Views tab turn up here a moment later; a
    // list baked in at generation time could only name what existed when this ran.
    static TopicBrowserUI CreateTopicsPage(Transform page)
    {
        const int rowCount = 7;

        var browserUi = page.gameObject.AddComponent<TopicBrowserUI>();

        var prevTargetButton = CreateButton(page, "\u25c0", new Vector2(-180f, 228f),
            new Vector2(48f, 52f), 22f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(prevTargetButton.onClick, browserUi.PreviousTarget);
        var targetLabel = CreateLabel(page, "Target Label", "", new Vector2(-20f, 228f),
            new Vector2(228f, 52f), 20f);
        var nextTargetButton = CreateButton(page, "\u25b6", new Vector2(124f, 228f),
            new Vector2(48f, 52f), 22f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(nextTargetButton.onClick, browserUi.NextTarget);

        // Detach the selected subscriber from its topic. Red and beside the name of the thing it
        // acts on, because it is the one control on this page that takes something away - and
        // the row list below is a list of topics you could pick, where an X per row would mean
        // "unsubscribe from a topic I am not on".
        var detachButton = CreateButton(page, "\u2715", new Vector2(180f, 228f),
            new Vector2(48f, 52f), 22f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(detachButton.onClick, browserUi.UnsubscribeTarget);

        var status = CreateLabel(page, "Status", "", new Vector2(0f, 186f),
            new Vector2(400f, 26f), 17f);
        status.color = k_TextMuted;

        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(page, "", new Vector2(0f, 148f - i * 46f),
                new Vector2(400f, 42f), 18f, true, Shade(k_ButtonNeutral, -0.25f));
            rows[i].gameObject.name = $"Topic Row {i}";
            // Topic names read better left-aligned, and they are long enough to want the room
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(14f, 0f);
        }

        // The list is a snapshot and nodes come and go while the app runs. It refreshes on open;
        // this is for the node you started after opening the tab.
        var refreshButton = CreateButton(page, "Refresh list", new Vector2(-102f, -180f),
            new Vector2(194f, 46f), 19f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(refreshButton.onClick, browserUi.Refresh);

        // "My topic isn't in the list" is nearly always the type filter, and until now the only
        // way to check that was a tickbox in the Inspector - which is not reachable from inside a
        // headset, where the question always gets asked.
        var typeFilterButton = CreateButton(page, "Types: matching", new Vector2(102f, -180f),
            new Vector2(194f, 46f), 19f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(typeFilterButton.onClick, browserUi.ToggleShowAllTypes);

        var prevButton = CreateButton(page, "\u25c0 Prev", new Vector2(-132f, -232f),
            new Vector2(130f, 46f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(prevButton.onClick, browserUi.PreviousPage);
        var pageLabel = CreateLabel(page, "Page Label", "1 / 1", new Vector2(0f, -232f),
            new Vector2(120f, 46f), 18f);
        pageLabel.color = k_TextMuted;
        var nextButton = CreateButton(page, "Next \u25b6", new Vector2(132f, -232f),
            new Vector2(130f, 46f), 18f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(nextButton.onClick, browserUi.NextPage);

        var browserSo = new SerializedObject(browserUi);
        browserSo.FindProperty("_targetLabel").objectReferenceValue = targetLabel;
        browserSo.FindProperty("_status").objectReferenceValue = status;
        browserSo.FindProperty("_pageLabel").objectReferenceValue = pageLabel;
        browserSo.FindProperty("_typeFilterLabel").objectReferenceValue =
            typeFilterButton.GetComponentInChildren<TextMeshProUGUI>();
        var rowsProp = browserSo.FindProperty("_rows");
        rowsProp.arraySize = rowCount;
        for (int i = 0; i < rowCount; i++)
            rowsProp.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
        browserSo.ApplyModifiedPropertiesWithoutUndo();

        return browserUi;
    }

    // Tells the spawner which objects it may copy. Kinds are named here, in the one place that
    // already knows how each visualisation was built and which handle belongs to it - the
    // alternative is a list in the Inspector that goes stale the next time this runs.
    //
    // The robot is deliberately absent. There is one robot: a second one would subscribe to a
    // second /joint_states and stand in the room beside the first, and the arm the chassis
    // calibration snaps onto has to be unambiguous.
    static void ConfigureSpawner(VisualizationSpawner spawner,
        (string Label, GameObject Template, GameObject Handle, float Spacing)[] kinds)
    {
        var so = new SerializedObject(spawner);
        var kindsProp = so.FindProperty("_kinds");
        kindsProp.arraySize = kinds.Length;
        for (int i = 0; i < kinds.Length; i++)
        {
            var element = kindsProp.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("Label").stringValue = kinds[i].Label;
            element.FindPropertyRelative("Template").objectReferenceValue = kinds[i].Template;
            element.FindPropertyRelative("Handle").objectReferenceValue =
                kinds[i].Handle != null ? kinds[i].Handle.GetComponent<PlacementHandle>() : null;
            element.FindPropertyRelative("SpacingMetres").floatValue = kinds[i].Spacing;
            // Six of anything is already more subscribers than the socket enjoys, and more
            // windows than there is room for around one robot
            element.FindPropertyRelative("MaxCount").intValue = 6;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Page: how many of each visualisation the scene has. A row per kind with a minus, the
    // count, and a plus.
    //
    // The count includes the scene's own instance, so it starts at 1 and never reaches 0 - the
    // generated one is the template every copy is made from, and destroying it would leave
    // nothing to copy and no way back. "I do not want this one" is the Scene tab (hide it) or
    // the Topics tab (detach its topic), both of which leave it recoverable.
    static VisualizationCountPanelUI CreateViewsPage(Transform page, VisualizationSpawner spawner,
        ControlPanelActions actions)
    {
        int rowCount = Mathf.Max(1, spawner.KindCount);

        var panelUi = page.gameObject.AddComponent<VisualizationCountPanelUI>();

        var status = CreateLabel(page, "Status", "", new Vector2(0f, 226f),
            new Vector2(400f, 26f), 17f);
        status.color = k_TextMuted;

        var rowLabels = new TextMeshProUGUI[rowCount];
        var countLabels = new TextMeshProUGUI[rowCount];
        var addButtons = new Button[rowCount];
        var removeButtons = new Button[rowCount];

        for (int i = 0; i < rowCount; i++)
        {
            float y = 166f - i * 64f;

            rowLabels[i] = CreateLabel(page, $"View Row {i}", "", new Vector2(-92f, y),
                new Vector2(216f, 48f), 20f, TextAlignmentOptions.Left);

            // Minus first, then the number, then plus: the order they read in, and the order the
            // count sits between the two things that change it
            removeButtons[i] = CreateButton(page, "\u2212", new Vector2(66f, y),
                new Vector2(50f, 48f), 24f, true, k_ButtonNegative);
            removeButtons[i].gameObject.name = $"View Remove {i}";

            countLabels[i] = CreateLabel(page, $"View Count {i}", "1", new Vector2(124f, y),
                new Vector2(46f, 48f), 22f);

            addButtons[i] = CreateButton(page, "+", new Vector2(182f, y),
                new Vector2(50f, 48f), 24f, true, k_ButtonPositive);
            addButtons[i].gameObject.name = $"View Add {i}";
        }

        CreateDivider(page, -110f, 400f);

        CreateHint(page, new Vector2(0f, -196f), new Vector2(400f, 120f),
            "A new copy arrives beside the one it was made from, with NO topic - give it one on " +
            "the Topics tab, where it appears as its own target. Copies carry their own grab " +
            "handle, TF anchor and Scene row. The minus removes the newest copy and detaches its " +
            "subscribers from the endpoint; the scene's own one cannot be removed.");

        var so = new SerializedObject(panelUi);
        so.FindProperty("_spawner").objectReferenceValue = spawner;
        so.FindProperty("_actions").objectReferenceValue = actions;
        so.FindProperty("_status").objectReferenceValue = status;

        SetArray(so, "_rowLabels", rowLabels);
        SetArray(so, "_countLabels", countLabels);
        SetArray(so, "_addButtons", addButtons);
        SetArray(so, "_removeButtons", removeButtons);
        so.ApplyModifiedPropertiesWithoutUndo();

        return panelUi;
    }

    static void SetArray(SerializedObject so, string propertyName, Object[] values)
    {
        var prop = so.FindProperty(propertyName);
        prop.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
    }

    // Page: TF placement. Which frame each visualisation would be placed by, whether that frame
    // has actually turned up in /tf, and a press per row to flip just that one.
    static TfAnchorPanelUI CreateFramesPage(Transform page, ControlPanelActions actions)
    {
        const int rowCount = 6;

        var panelUi = page.gameObject.AddComponent<TfAnchorPanelUI>();

        var status = CreateLabel(page, "Status", "", new Vector2(-70f, 226f),
            new Vector2(260f, 46f), 17f);
        status.color = k_TextMuted;

        // Paged since visualisations can be duplicated at runtime. Up beside the status rather
        // than below the rows: the rows are two lines tall and already reach the hint.
        var prevPageButton = CreateButton(page, "\u25c0", new Vector2(96f, 226f),
            new Vector2(38f, 36f), 17f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(prevPageButton.onClick, panelUi.PreviousPage);
        var pageLabel = CreateLabel(page, "Page Label", "1 / 1", new Vector2(140f, 226f),
            new Vector2(44f, 36f), 16f);
        pageLabel.color = k_TextMuted;
        var nextPageButton = CreateButton(page, "\u25b6", new Vector2(184f, 226f),
            new Vector2(38f, 36f), 17f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(nextPageButton.onClick, panelUi.NextPage);

        // Two buttons that each do exactly what they say, replacing one button whose label
        // flipped between naming the state and naming the action - which has to be read twice,
        // and always at the moment you are looking at the robot rather than at the panel.
        //
        // Wired to ControlPanelActions rather than to this panel's own AllTf/AllManual because
        // those report into the footer, including WHY a frame did not resolve - which is the
        // entire question when the press appears to do nothing.
        var allTfButton = CreateButton(page, "All \u2192 TF", new Vector2(-102f, 172f),
            k_ButtonSize, 20f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(allTfButton.onClick, actions.AnchorAllTf);
        var allManualButton = CreateButton(page, "All \u2192 Manual", new Vector2(102f, 172f),
            k_ButtonSize, 20f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(allManualButton.onClick, actions.AnchorAllManual);

        // Two lines per row (name + frame, then state), so these are taller than the topic
        // browser's - the frame name is the whole point of this page and truncating it would
        // defeat it
        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(page, "", new Vector2(0f, 112f - i * 56f),
                new Vector2(400f, 52f), 17f, true, Shade(k_ButtonNeutral, -0.25f));
            rows[i].gameObject.name = $"Anchor Row {i}";
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(14f, 0f);
        }

        CreateHint(page, new Vector2(0f, -228f), new Vector2(400f, 60f),
            "A press on a row flips that one visualisation. On TF, the white TF origin is the " +
            "only thing left to line up with the real robot - everything else follows /tf.");

        var so = new SerializedObject(panelUi);
        so.FindProperty("_status").objectReferenceValue = status;
        so.FindProperty("_pageLabel").objectReferenceValue = pageLabel;
        var rowsProp = so.FindProperty("_rows");
        rowsProp.arraySize = rowCount;
        for (int i = 0; i < rowCount; i++)
            rowsProp.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        return panelUi;
    }

    // The rig that reads the passthrough camera and moves the robot onto the chassis tag. Kept
    // as its own root object rather than hung off the robot, because what it ends up moving may
    // be the TF origin instead - it is a tool that acts on the scene, not a part of the robot.
    static ArucoRobotCalibrator CreateArucoCalibrationRig(GameObject robot, GameObject robotHandle)
    {
        var rigGo = new GameObject("ArUco Calibration");

        var feed = rigGo.AddComponent<PassthroughCameraFeed>();
        var calibrator = rigGo.AddComponent<ArucoRobotCalibrator>();

        // Drawn where the calibration thinks the tag is, so a wrong head-to-camera offset shows
        // up as a square floating off the real tag rather than as a quietly misplaced robot
        var gizmoGo = new GameObject("Marker Pose Gizmo", typeof(MeshFilter), typeof(MeshRenderer));
        gizmoGo.transform.SetParent(rigGo.transform, false);
        var gizmo = gizmoGo.AddComponent<MarkerPoseGizmo>();

        // The tracked head, from whichever Camera Rig Building Block is in the scene. Searched
        // by name because the rig is added by hand (Building Blocks aren't scriptable) and so
        // cannot be referenced by anything this generator created.
        Transform head = FindTransformByName("CenterEyeAnchor");
        if (head == null)
        {
            Debug.LogWarning("[XRViz] No CenterEyeAnchor found, so the calibration rig has no " +
                "head transform to measure the camera out from. Add the [Camera Rig] Building " +
                "Block, then assign it on ArUco Calibration > Passthrough Camera Feed.");
        }

        var feedSo = new SerializedObject(feed);
        feedSo.FindProperty("_headAnchor").objectReferenceValue = head;
        feedSo.ApplyModifiedPropertiesWithoutUndo();

        var so = new SerializedObject(calibrator);
        so.FindProperty("_feed").objectReferenceValue = feed;
        so.FindProperty("_gizmo").objectReferenceValue = gizmo;
        so.FindProperty("_robotHandle").objectReferenceValue = robotHandle.GetComponent<PlacementHandle>();
        so.FindProperty("_robotAnchor").objectReferenceValue = robot.GetComponent<TfAnchor>();
        so.ApplyModifiedPropertiesWithoutUndo();

        return calibrator;
    }

    static Transform FindTransformByName(string name)
    {
        // Include inactive: the Camera Rig's anchors are live in a build but a scene opened in
        // the Editor may well have parts of the rig switched off
        var transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        foreach (var transform in transforms)
        {
            if (transform.name == name)
                return transform;
        }
        return null;
    }

    // Page: chassis-tag calibration. A tab rather than a panel 0.9 m off to the right, because
    // this is the one page used while walking around looking at the real robot - it should be
    // where the others are, not somewhere you have to turn away from the tag to read.
    static CalibrationPanelUI CreateCalibratePage(Transform page, ArucoRobotCalibrator calibrator)
    {
        var panelUi = page.gameObject.AddComponent<CalibrationPanelUI>();

        // Generously tall and top-aligned: the status line carries the failure messages, and
        // those name what actually went wrong, which takes more than one line to say
        var status = CreateLabel(page, "Status", "", new Vector2(0f, 110f),
            new Vector2(400f, 270f), 18f, TextAlignmentOptions.TopLeft);
        status.color = k_TextPrimary;

        var progressFill = CreateProgressBar(page, -46f, 400f, 14f);

        var startButton = CreateButton(page, "Find Tag", new Vector2(0f, -104f),
            new Vector2(260f, 56f), 21f, true, k_ButtonAccent);
        UnityEventTools.AddVoidPersistentListener(startButton.onClick, calibrator.BeginCalibration);

        // Apply and Cancel share the row with Find Tag; CalibrationPanelUI shows only the pair
        // that makes sense for the state it is in
        var applyButton = CreateButton(page, "Apply", new Vector2(-102f, -104f),
            k_ButtonSize, 21f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(applyButton.onClick, calibrator.ConfirmCalibration);
        var cancelButton = CreateButton(page, "Cancel", new Vector2(102f, -104f),
            k_ButtonSize, 21f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(cancelButton.onClick, calibrator.CancelCalibration);

        CreateHint(page, new Vector2(0f, -190f), new Vector2(400f, 100f),
            "Android only - the headset camera cannot be opened over Quest Link or in the " +
            "Editor, so this reports itself unavailable there.\n\nPrint the tag from " +
            "XRViz > Export ArUco Calibration Tag, lie it flat on the chassis, and stand where " +
            "you can see it.");

        var so = new SerializedObject(panelUi);
        so.FindProperty("_calibrator").objectReferenceValue = calibrator;
        so.FindProperty("_status").objectReferenceValue = status;
        so.FindProperty("_startButton").objectReferenceValue = startButton;
        so.FindProperty("_applyButton").objectReferenceValue = applyButton;
        so.FindProperty("_cancelButton").objectReferenceValue = cancelButton;
        so.FindProperty("_progressFill").objectReferenceValue = progressFill;
        so.ApplyModifiedPropertiesWithoutUndo();

        return panelUi;
    }

    // Track plus a filled bar. Returns the fill, whose parent is the track - the panel hides the
    // whole thing by deactivating that parent when there is no search running.
    static Image CreateProgressBar(Transform parent, float y, float width, float height)
    {
        var trackGo = new GameObject("Search Progress", typeof(RectTransform), typeof(Image));
        trackGo.transform.SetParent(parent, false);
        var trackRect = (RectTransform)trackGo.transform;
        trackRect.anchorMin = new Vector2(0.5f, 0.5f);
        trackRect.anchorMax = new Vector2(0.5f, 0.5f);
        trackRect.pivot = new Vector2(0.5f, 0.5f);
        trackRect.anchoredPosition = new Vector2(0f, y);
        trackRect.sizeDelta = new Vector2(width, height);
        var trackImage = trackGo.GetComponent<Image>();
        trackImage.sprite = GetBuiltinSprite("UISprite");
        trackImage.type = Image.Type.Sliced;
        trackImage.color = k_ButtonNeutral;
        trackImage.raycastTarget = false;

        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(trackGo.transform, false);
        var fillRect = (RectTransform)fillGo.transform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        var fillImage = fillGo.GetComponent<Image>();
        fillImage.sprite = GetBuiltinSprite("UISprite");
        fillImage.type = Image.Type.Filled;
        fillImage.fillMethod = Image.FillMethod.Horizontal;
        fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
        fillImage.fillAmount = 0f;
        fillImage.color = k_ButtonAccent;
        fillImage.raycastTarget = false;

        trackGo.SetActive(false);
        return fillImage;
    }

    // Page: what is in the room, and what state it is holding. Every VisibilityTarget gets a row
    // that shows or hides just that one, and the two scene-wide actions live here too - they are
    // both answers to "this visualisation is wrong", which is the question this page is for.
    static VisibilityPanelUI CreateScenePage(Transform page, ControlPanelActions actions)
    {
        const int rowCount = 6;

        var panelUi = page.gameObject.AddComponent<VisibilityPanelUI>();

        var status = CreateLabel(page, "Status", "", new Vector2(-70f, 230f),
            new Vector2(260f, 26f), 17f);
        status.color = k_TextMuted;

        // Same pager as the TF page, for the same reason: the Views tab can put more
        // visualisations in the scene than six rows hold.
        var prevPageButton = CreateButton(page, "\u25c0", new Vector2(96f, 230f),
            new Vector2(38f, 34f), 17f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(prevPageButton.onClick, panelUi.PreviousPage);
        var pageLabel = CreateLabel(page, "Page Label", "1 / 1", new Vector2(140f, 230f),
            new Vector2(44f, 34f), 16f);
        pageLabel.color = k_TextMuted;
        var nextPageButton = CreateButton(page, "\u25b6", new Vector2(184f, 230f),
            new Vector2(38f, 34f), 17f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(nextPageButton.onClick, panelUi.NextPage);

        // One line per row (label + shown/hidden), unlike the TF page's two - there is no frame
        // name to make room for here
        var rows = new Button[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = CreateButton(page, "", new Vector2(0f, 190f - i * 44f),
                new Vector2(400f, 40f), 18f, true, Shade(k_ButtonNeutral, -0.25f));
            rows[i].gameObject.name = $"Visibility Row {i}";
            var rowLabel = rows[i].GetComponentInChildren<TextMeshProUGUI>();
            rowLabel.alignment = TextAlignmentOptions.Left;
            rowLabel.rectTransform.offsetMin = new Vector2(14f, 0f);
        }

        var showAllButton = CreateButton(page, "Show All", new Vector2(-102f, -90f),
            k_ButtonSize, 20f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(showAllButton.onClick, panelUi.ShowAll);
        var hideAllButton = CreateButton(page, "Hide All", new Vector2(102f, -90f),
            k_ButtonSize, 20f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(hideAllButton.onClick, panelUi.HideAll);

        CreateDivider(page, -130f, 400f);

        var clearDataButton = CreateButton(page, "Clear Data", new Vector2(-102f, -172f),
            k_ButtonSize, 20f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(clearDataButton.onClick, actions.ClearVisualizations);
        var resetButton = CreateButton(page, "Reset Layout", new Vector2(102f, -172f),
            k_ButtonSize, 20f, true, k_ButtonNeutral);
        UnityEventTools.AddVoidPersistentListener(resetButton.onClick, actions.ResetAnchors);

        CreateHint(page, new Vector2(0f, -230f), new Vector2(400f, 56f),
            "Clear Data wipes held geometry; a visualisation also clears itself after a few " +
            "seconds of silence. Reset Layout puts every handle - this panel included - back " +
            "where it started.");

        var so = new SerializedObject(panelUi);
        so.FindProperty("_status").objectReferenceValue = status;
        so.FindProperty("_pageLabel").objectReferenceValue = pageLabel;
        var rowsProp = so.FindProperty("_rows");
        rowsProp.arraySize = rowCount;
        for (int i = 0; i < rowCount; i++)
            rowsProp.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        return panelUi;
    }

    // Page: the numeric keypad for retyping the ROS IP at runtime. No native VR keyboard is
    // installed in this project's packages, so there is nothing to hook a TMP_InputField up to.
    //
    // It has no tab of its own: it is a step inside changing the IP, reached from the ROS page
    // and returning there, rather than a place you would ever choose to go.
    static IpKeypadUI CreateKeypadPage(Transform page, RosConnectionStatusUI statusUi,
        ControlPanelTabs tabs)
    {
        var keypadUi = page.gameObject.AddComponent<IpKeypadUI>();

        var displayGo = new GameObject("Display", typeof(TextMeshProUGUI));
        displayGo.transform.SetParent(page, false);
        var display = displayGo.GetComponent<TextMeshProUGUI>();
        display.fontSize = 30f;
        display.color = k_TextPrimary;
        display.raycastTarget = false;
        display.alignment = TextAlignmentOptions.Center;
        var displayRect = display.rectTransform;
        displayRect.anchorMin = displayRect.anchorMax = displayRect.pivot = new Vector2(0.5f, 0.5f);
        displayRect.anchoredPosition = new Vector2(0f, 208f);
        displayRect.sizeDelta = new Vector2(380f, 58f);

        string[,] grid =
        {
            { "1", "2", "3" },
            { "4", "5", "6" },
            { "7", "8", "9" },
            { ".", "0", "back" },
        };
        for (int row = 0; row < 4; row++)
        {
            float y = 132f - row * 62f;
            for (int col = 0; col < 3; col++)
            {
                float x = -110f + col * 110f;
                string key = grid[row, col];
                bool isBackspace = key == "back";
                var keyButton = CreateButton(page, isBackspace ? "\u232b" : key,
                    new Vector2(x, y), new Vector2(100f, 56f), 24f, true, k_ButtonNeutral);
                if (isBackspace)
                    UnityEventTools.AddVoidPersistentListener(keyButton.onClick, keypadUi.Backspace);
                else
                    UnityEventTools.AddStringPersistentListener(keyButton.onClick, keypadUi.AppendChar, key);
            }
        }

        var clearButton = CreateButton(page, "Clear", new Vector2(-102f, -132f),
            k_ButtonSize, 20f, true, k_ButtonNegative);
        UnityEventTools.AddVoidPersistentListener(clearButton.onClick, keypadUi.Clear);

        // Apply saves the address and reconnects; the second listener walks back to the page
        // that sent you here, so a finished edit doesn't leave you staring at a keypad
        var applyButton = CreateButton(page, "Apply", new Vector2(102f, -132f),
            k_ButtonSize, 20f, true, k_ButtonPositive);
        UnityEventTools.AddVoidPersistentListener(applyButton.onClick, keypadUi.Apply);
        UnityEventTools.AddIntPersistentListener(applyButton.onClick, tabs.ShowPage, 0);

        var backButton = CreateButton(page, "Back", new Vector2(0f, -204f),
            new Vector2(200f, 46f), 19f, true, k_ButtonNeutral);
        UnityEventTools.AddIntPersistentListener(backButton.onClick, tabs.ShowPage, 0);

        var keypadSo = new SerializedObject(keypadUi);
        keypadSo.FindProperty("_display").objectReferenceValue = display;
        keypadSo.FindProperty("_statusUi").objectReferenceValue = statusUi;
        keypadSo.ApplyModifiedPropertiesWithoutUndo();

        return keypadUi;
    }

    // Page: the handle colour key, and the half-dozen things nobody can discover by looking.
    // This is where the key went when the main panel stopped carrying it - it is reference
    // material you read once, and it was taking up a third of the panel on every press.
    static void CreateGuidePage(Transform page)
    {
        CreateAnchorKey(page, headingY: 232f, firstRowY: 200f, rowStep: 26f);
        CreateDivider(page, 24f, 400f);

        var text = CreateLabel(page, "Guide Text",
            "<b>Show or hide this panel</b>\nMenu button on the left controller, or Hide at the " +
            "foot of the tabs.\n\n" +
            "<b>Move anything</b>\nGrab its coloured sphere - reach out and grab it, or point at " +
            "it from a distance and hold the trigger. The panel has one of its own, below it.\n\n" +
            "<b>Lost the panel</b>\nPress Menu while facing away from it and it comes to you.\n\n" +
            "<b>Nothing is drawing</b>\nPick topics on the Topics tab: the laser scan and the " +
            "point cloud start with none.\n\n" +
            "<b>Two of something</b>\nViews tab, + on its row. The copy arrives beside the " +
            "original with no topic; give it one on Topics. The red X there detaches a " +
            "subscriber from its topic again.",
            new Vector2(0f, -110f), new Vector2(400f, 250f), 16f, TextAlignmentOptions.TopLeft);
        text.color = k_TextPrimary;
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
