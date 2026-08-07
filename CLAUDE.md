# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

XRViz is a Unity project providing XR (Meta Quest / OpenXR) visualization and teleoperation tools for ROS 2 robots. There is no standalone build system, test suite, or CI — all building, running, and testing happens through the Unity Editor.

- **Unity version**: 6000.3.11f1 (pinned in `ProjectSettings/ProjectVersion.txt`) — open via Unity Hub.
- **Targets**: Meta Quest 3 mixed reality on the OpenXR loader, run two ways: on a Windows desktop over **Quest Link** (Editor Play Mode or Windows player — the primary dev loop) or as a standalone Android APK (package `com.UTS.XRViz`). Meta XR Simulator for headset-free smoke tests.
- **XR stack direction**: Meta XR SDK **Building Blocks** (Camera Rig, Passthrough, Interaction SDK grabs) are the preferred way to assemble XR scenes — not AR Foundation/XRI rigs. Packages: `com.meta.xr.sdk.core` + `com.meta.xr.sdk.interaction.ovr`.
- **Scenes**: `Assets/XRViz/Scenes/MVP_UR3e_MR.unity` (MR MVP — see `Docs/MVP_QUEST3_SETUP.md`; regenerate via menu **XRViz → Create MR MVP Scene (UR3e)**) and `RobotsGallery.unity` (robot gallery, has no XR rig of its own).
- All scripts compile into the default `Assembly-CSharp` (no asmdefs). `.csproj`/`.sln` files are Unity-generated and gitignored; `XRViz.slnx` is the checked-in solution wrapper.

### Required external pieces

- **ROS side**: a running `ros_tcp_endpoint` on the ROS 2 machine. The connection IP/port is serialized in `Assets/Resources/ROSConnectionPrefab.prefab` (`m_RosIPAddress`, `m_RosPort` — currently 192.168.50.250:10000).
- **LightBuzz JPEG plugin**: `Assets/LightBuzz_Jpeg*` is gitignored (paid asset) but required — `RosSubscriberCompressedImage.cs` won't compile without it. It must be installed locally.

## Architecture

ROS integration is built on Unity's ROS-TCP-Connector package (`ROSConnection.GetOrCreateInstance()` singleton). Custom code lives in `Assets/XRViz/Scripts/` (namespace `Unity.Robotics`) in three layers:

1. **`ROS/Publishers/` and `ROS/Subscribers/`** — thin per-message-type wrappers around two generic abstract bases:
   - `RosPublisher<T>` (`Publishers/RosPublisher.cs`): registers the topic in `Start()`, exposes `Publish()`/`SetTopic()`.
   - `RosSubscriber<T>` (`Subscribers/RosSubscriber.cs`): receives on the ROS callback thread into a queue, consumed on the main thread. Subclasses typically expose `isReady()` plus type-specific accessors (e.g. `RosSubscriberCompressedImage` decodes JPEG/PNG into a `Texture2D`).
   - Adding support for a new message type = new small subclass in the matching folder.

2. **`ROS/MessageHandling/`** — MonoBehaviours that wire subscribers/publishers to scene behavior, grouped by role:
   - `Robot/`: drives URDF-imported robots kinematically. `JointStateWriter` (one per `ArticulationBody` joint) sets joint positions directly; `RobotStateWriterController` maps joint names → writers and handles URDF mimic joints; `RobotStateWriterControllerRos` feeds it from a `RosSubscriberJointState`.
     - **Runtime scripts must not reference `UrdfImporter` types.** The URDF Importer's runtime assembly is desktop-only (`includePlatforms: Editor/Win64/Linux64/macOS` — it ships native AssimpNet + VHACD with no Android binary), so any such reference breaks the Quest APK build with `CS0246` while compiling fine in the Editor. Joint names come from the baked `UrdfJointName` component (**XRViz → Bake URDF Joint Names**) and collision meshes from the child named `Collisions`. See `Docs/MVP_QUEST3_SETUP.md`.
   - `Sensor/`: camera images to textures/meshes, IMU, and the GPU point cloud (below).
   - `Control/`: publishing twist/joint commands from XR interactions (e.g. grabbable end effector).
   - `User/`: publishes XR headset/controller tracking to ROS.
   - `Visualizations/`: marker rendering (cubes, line strips) and `LaserScanVisualizer`.

   Two rules for anything new in here:
   - **Convert coordinates with `FLU.ConvertToRUF`** from `Unity.Robotics.ROSTCPConnector.ROSGeometry` — ROS is right-handed Z-up, Unity left-handed Y-up. Don't hand-roll it. (`RosSubscriberPointCloud2` predates this rule and does no conversion — its clouds land rotated and mirrored.)
   - **Draw into a Mesh under a MeshFilter, not `Graphics.DrawProcedural`.** DrawProcedural renders in world space and ignores the GameObject's Transform, so a `PlacementHandle` can't move it. This is why `PointCloudRosGPU_PointCloud2` isn't handle-movable.

   Visualisations need an unlit, vertex-colour-capable, stereo-aware shader: `Shaders/VertexColorUnlit.shader`. Built-in `Unlit/Color` ignores vertex colours; lit shaders render near-black under passthrough; and without the `UNITY_VERTEX_OUTPUT_STEREO` macros a mesh draws to one eye only on Quest. `Shaders/HandleUnlit.shader` is the solid-colour counterpart for meshes with no vertex colour channel (the handle spheres) — same stereo macros, plus a baked shape ramp so a sphere doesn't read as a flat disc.

3. **`Utils/`** — e.g. `CompressedDepthPNGDecoder` for ROS `compressedDepth` (16-bit PNG) payloads.

4. **`Interaction/`** — `PlacementHandle`, the one generic grab handle: a grabbable sphere that repositions whatever it points at (`_target` for a Transform, `_targetArticulationBody` for a robot root, which must be moved with `TeleportRoot`). It captures its start pose at `Awake` and `ResetToDefault()` returns to it. Handles are colour-coded; the colours are defined once in `k_HandleStyles` (`XRVizCreateMVPScene.cs`) and drive both the generated materials in `Assets/XRViz/Materials/` and the panel's key — add a handle there, not ad hoc, or the key goes stale.

5. **`UI/`** — the world-space ROS control panel. `TopicBrowserUI` lists what the endpoint advertises (`ROSConnection.GetTopicAndTypeList`) and rebinds a subscriber live via `IRosTopicBinding`, which `RosSubscriber<T>` implements — `SetTopic` is plain configuration before `Start` and an unsubscribe/resubscribe afterwards. `ControlPanelActions` holds the buttons that act on the rest of the scene (clear the laser scan, reset the anchors); it locates targets with `FindObjectsByType` at press time rather than a serialized list, so hand-added objects are picked up.

   Panels are styled by `StylePanel` in the scene generator: a near-opaque dark background plus a header bar. The background is functional, not decoration — a translucent canvas over passthrough video of a light wall is unreadable. Anything new floating in world space needs the same treatment.

   A visualiser that draws into a mesh keeps drawing after its topic goes quiet, and a frozen frame looks like a live one. Give it an arrival-time staleness timeout (`Time.realtimeSinceStartup` at receipt, *not* the message header stamp) and a `Clear()` that also clears the subscriber's parsed state — clearing only the mesh lets the next frame rebuild it from the cached message. `LaserScanVisualizer` is the reference.

### GPU point cloud pipeline

`PointCloudRosGPU.cs` pairs color+depth `CompressedImage` subscribers with their `CameraInfo` subscribers, waits for both intrinsics via coroutine, then each frame uploads the two textures to `Assets/XRViz/Shaders/PointCloudReconstructionGPU.compute`, which writes position/color compute buffers rendered with `Graphics.DrawProcedural` and the `PointCloudSquares*` shaders (point topology, no mesh). Dispatch is `width/8 × height/8`, so image dimensions must be divisible by 8. Compute buffers are sized from the **color** camera info and released in `OnDestroy`.

### Robot assets

`Assets/XRViz/Robots/` holds URDF-imported robots (g1, pr2, ur_onrobot, realsense) as prefabs with `ArticulationBody` chains, imported via the Unity URDF-Importer package. The `ur3e_rg2` and other robot prefabs already carry the full joint-state wiring (subscriber on `/joint_states`, state writers, mimic joints) serialized in the prefab — instantiating them is enough.

### Editor tooling (`XRViz` menu)

`Assets/XRViz/Scripts/Editor/` adds menu items that operate on the selected GameObject:
- **XRViz → Add Joint State Components**: wires `RosSubscriberJointState` + state writer components onto a URDF-imported robot and parses the URDF XML to configure mimic joints.
- **XRViz → Fix Missing Meshes**: reassigns missing `MeshFilter`/`MeshCollider` meshes by matching asset name to GameObject name.

### MR robot registration

`Assets/XRViz/MRRobotRegistrationTool/` aligns the virtual robot with the physical one: the user places ≥3 points on the real robot, then the ≥3 corresponding points on the virtual model; `RobotRegistrationTool` computes a centroid translation + rotation (from the first two point pairs) and teleports the robot's `ArticulationBody` root.
