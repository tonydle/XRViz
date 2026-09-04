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
- **No LightBuzz JPEG plugin is needed** (this file used to claim otherwise). `RosSubscriberCompressedImage.cs` decodes with Unity's built-in `ImageConversion.LoadImage` plus the project's own `Utils/CompressedDepthPNGDecoder`; nothing under `Assets/` references LightBuzz, it isn't in `.gitignore`, and the folder isn't present. The project compiles without it.

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
     - **Exception: camera optical frames.** A `*_optical_frame` is right-down-forward (REP 103/145), not FLU, so the conversion is a plain Y flip — `(x, -y, z)`. Using `FLU.ConvertToRUF` there lands the cloud on its side, which is what `PointCloudReconstructionGPU.compute` does.
     - The two conventions differ by an **axis permutation, not a flip**, so mixing them costs a rotation rather than a sign. TF transforms are converted as FLU like everything else, so data drawn in the optical convention needs that difference rotated back out: `TfAnchor.k_OpticalCorrection`, a −120° rotation about `(1,1,1)`, post-multiplied onto the TF rotation. Sanity check if it ever looks wrong: a forward-looking camera must come out with **identity** rotation relative to its parent link.
   - **The GameObject's Transform must place the visualisation**, so a `PlacementHandle` can move it. Usually that means drawing into a Mesh under a MeshFilter, because `Graphics.DrawProcedural` renders in world space and ignores the Transform — that's why `PointCloudRosGPU_PointCloud2` isn't handle-movable. A GPU pipeline may instead pass `transform.localToWorldMatrix` into the compute shader and bake it into the emitted positions (`DepthImagePointCloud`, and `PointCloudRosGPU` already did this); then remember to recentre the bounds passed to the draw call each frame or it gets frustum-culled once moved.

   Visualisations need an unlit, vertex-colour-capable, stereo-aware shader: `Shaders/VertexColorUnlit.shader`. Built-in `Unlit/Color` ignores vertex colours; lit shaders render near-black under passthrough; and without the `UNITY_VERTEX_OUTPUT_STEREO` macros a mesh draws to one eye only on Quest. `Shaders/HandleUnlit.shader` is the solid-colour counterpart for meshes with no vertex colour channel (the handle spheres) — same stereo macros, plus a baked shape ramp so a sphere doesn't read as a flat disc.

3. **`Utils/`** — e.g. `CompressedDepthPNGDecoder` for ROS `compressedDepth` (16-bit PNG) payloads.

4. **`Interaction/`** — `PlacementHandle`, the one generic grab handle: a grabbable sphere that repositions whatever it points at (`_target` for a Transform, `_targetArticulationBody` for a robot root, which must be moved with `TeleportRoot`). It captures its start pose at `Awake` and `ResetToDefault()` returns to it. Handles are colour-coded; the colours are defined once in `k_HandleStyles` (`XRVizCreateMVPScene.cs`) and drive both the generated materials in `Assets/XRViz/Materials/` and the panel's key — add a handle there, not ad hoc, or the key goes stale.

   `TfAnchor` is the other way to place the same objects: from `/tf` instead of by hand, toggled at runtime. Only one may own a pose at a time, so an anchor switched on calls `PlacementHandle.SetSuspended(true)` (which hides the sphere and stops its writes) and drives the target through `SetTargetPose`; switching back calls `SnapToTarget()` so the handle re-homes onto wherever TF left the object instead of yanking it back. Anything new that should be TF-placeable gets a `TfAnchor` plus an `IRosFrameSource` subscriber so the frame follows the topic — there is no keyboard in the headset to retype a frame name with.

   Anchors resolve against **`ROS/Tf/RosTfTree`**, and *that component's Transform is the fixed frame* — the whole TF world is measured out from it, so it carries the white TF origin handle and is the one thing that needs aligning with the real room. Two things it does that the package's `TFSystem` does not, and the reasons not to switch back:
   - It collects `/tf` **and** `/tf_static` into **one** frame table. `TFSystem` keeps a separate table per TF topic, so the normal case — a static sensor mount under a moving arm — cannot be resolved at all.
   - It reports failure. An unknown, stale, or different-tree frame returns false and the anchor leaves the object where it is; `TFSystem.GetTransform` returns identity, which silently places the data at the origin as if that were a measurement.

   `TFSystem`/`TFStream` themselves are safe to reference — they live in `Unity.Robotics.ROSTCPConnector`, which includes Android. **`Unity.Robotics.Visualizations` is not**: its runtime asmdef is `Editor/Linux64/macOS/Win32/Win64` only, the same desktop-only trap as `UrdfImporter`, so `TFSystemVisualizer` and friends break the Quest APK while compiling fine in the Editor.

5. **`UI/`** — the world-space ROS control panel. `TopicBrowserUI` lists what the endpoint advertises (`ROSConnection.GetTopicAndTypeList`) and rebinds a subscriber live via `IRosTopicBinding`, which `RosSubscriber<T>` implements — `SetTopic` is plain configuration before `Start` and an unsubscribe/resubscribe afterwards. `ControlPanelActions` holds the buttons that act on the rest of the scene (clear the laser scan, reset the anchors, flip every `TfAnchor` between TF and hand placement); it locates targets with `FindObjectsByType` at press time rather than a serialized list, so hand-added objects are picked up. `TfAnchorPanelUI` is the per-visualisation counterpart, listing each anchor's frame and whether it resolved.

   Panels are styled by `StylePanel` in the scene generator: a near-opaque dark background plus a header bar. The background is functional, not decoration — a translucent canvas over passthrough video of a light wall is unreadable. Anything new floating in world space needs the same treatment.

   A visualiser that holds geometry keeps drawing after its topic goes quiet, and a frozen frame looks like a live one. Implement `IClearableVisualization` (which the panel's **Clear Data** button discovers automatically), give it an arrival-time staleness timeout (`Time.realtimeSinceStartup` at receipt, *not* the message header stamp), and make `Clear()` also clear the subscriber's parsed state — clearing only the geometry lets the next frame rebuild it from the cached message. `LaserScanVisualizer` and `DepthImagePointCloud` are the references.

6. **`Calibration/`** — snapping the virtual robot onto the real one from an ArUco tag on its chassis, seen through the passthrough camera. Full guide: `Docs/ARUCO_CALIBRATION.md`. `ArucoDetector` + `MarkerPoseSolver` + `ArucoDictionary` are a self-contained ArUco implementation (no OpenCV, no native plugin — the same desktop-only-assembly trap as `UrdfImporter` would otherwise apply), `PassthroughCameraFeed` supplies frames, and `ArucoRobotCalibrator` runs the search → confirm → apply flow.

   - **Android only.** `horizonos.permission.HEADSET_CAMERA` does not exist over Quest Link or in the Editor, so this is the one feature that cannot be tested in the primary dev loop; it reports itself unavailable there rather than hanging. **XRViz → Check ArUco Detector** runs the whole pipeline over synthetic images so the maths can be checked from the desk — run it after touching anything here.
   - **The dictionary tables in `ArucoDictionary.cs` are data, not derivable.** They were dumped from opencv-contrib and verified entry by entry against what `cv2.aruco.generateImageMarker` draws. Don't hand-edit them; a wrong entry shows up only as a tag that never decodes.
   - **Three conventions this code lives or dies by**, all of which look almost right when broken: the detector reports corner pixel centres at **+0.5** (matching what `cx`/`cy` mean); `GetPixels32` returns the **bottom row first** and is flipped during grayscale conversion so rows run top-down from there on; and `MarkerPose.Rotation` gives a correct `forward` (out of the printed face) and `up` (top of the print) but a **mirrored `right`**, because the OpenCV→Unity Y flip is a reflection and one axis has to give.
   - **The headset names its cameras twice and the names do not match.** camera2 calls them `1`/`50`/`51`; Unity's `WebCamTexture.devices` calls the same three `Camera 0`/`1`/`2`. Only camera2's vendor metadata identifies which is the passthrough pair, and only Unity's names open anything — give `WebCamTexture` a camera2 id and it logs `Cannot find webcam device 50` and returns a texture that never plays, i.e. **no frames**, which downstream looks exactly like a dark room. `ResolveDevice` maps between the lists by index (guarded on equal lengths); `ReadDeviceCalibration` still needs the camera2 **id**, because the factory intrinsics are keyed by it. Also keep the requested resolution at the sensor's square **1280x1280** active array so the reported intrinsics need no rescaling — 1280x960 is a crop, not a resize, and `ScaledTo` would quietly get `fy` 25% wrong. Full detail in `Docs/ARUCO_CALIBRATION.md`.
   - **Android's `LENS_POSE_ROTATION` is a frame conversion, not a lens tilt.** It maps the device frame into the camera's own (z along the optical axis, y down the image), so for any outward-facing camera it contains a ~180° flip about x — a Quest 3 reports 169.2° about −x. Applied as a head→camera tilt it aims the camera backwards and upside down; detection still works (image space), so the tag reads fine and only the placement moves, putting the robot metres away. The conversion is `(x,y,z,w)_android -> (w,-z,-y,-x)_unity`, and `ReadDeviceCalibration` rejects any result more than 45° off head-forward — the right answer is ~11° down.
   - **A wrong vertical flip mirrors the image, and a mirror is not a rotation.** `TryIdentify` searches the four rotations only, so a reflected feed traces contours, accepts quads, and then decodes nothing — reported as "wrong dictionary", which is the wrong advice. `ArucoDictionary.IdentifiesWhenMirrored` exists purely to tell those apart and is never a route to accepting a marker; the feed's `VerticalFlip` (Auto/Flip/DontFlip) is the fix. Note some codes are symmetric under reflection (`Dict4x4_50`: **8, 9, 17, 47**) and decode to their own id when mirrored, giving a silently reflected pose instead of a failure — don't print those. **XRViz → Check ArUco Detector** lists them.
   - **A planar tag is worst at height**, and height is also where a wrong marker size, lens offset or tag mounting height all land. So `ArucoRobotCalibrator.VerticalMode` defaults to `KeepRobotHeight`: horizontal position and yaw from the tag, height left alone (making `_tagHeightAboveBase` unused), then trimmed by thumbstick during the confirm step. The trim moves whatever was moved — the robot, or the TF origin so scan and cloud follow — and never touches the undo pose, so Cancel still works. The gizmo stays at the *measured* tag pose, since that is the check on the camera geometry.
   - **What a calibration moves is not always the robot.** When the robot is anchored to TF, moving the robot is pointless — TF puts it back next frame — so the calibrator moves the `RosTfTree` transform instead, which drags the scan and cloud along consistently. Anything else that places things from a measurement needs the same distinction.

### GPU point cloud pipelines

**Current (`DepthImagePointCloud.cs`)** — the RGBD path to use. Raw `sensor_msgs/Image` colour + depth (`RosSubscriberImage` picks the texture format from the message's `encoding`, so `32FC1` metres and `16UC1` millimetres both work) plus the depth `CameraInfo`, reconstructed by `Shaders/DepthImagePointCloudGPU.compute` and drawn with `Shaders/PointCloudBillboard.shader` — six vertices per point expanded in the vertex shader from `SV_VertexID`, no geometry shader, stereo-aware. The visualiser's Transform is the cloud's origin (see the Transform rule above). Bandwidth, not GPU, is the limit: raw 640×480 colour + depth at 30 Hz is ~60 MB/s over the TCP socket — throttle on the ROS side.

**Legacy (`PointCloudRosGPU.cs`)** — pairs color+depth `CompressedImage` subscribers with their `CameraInfo` subscribers, waits for both intrinsics via coroutine, then each frame uploads the two textures to `Assets/XRViz/Shaders/PointCloudReconstructionGPU.compute`, which writes position/color compute buffers rendered with `Graphics.DrawProcedural` and the `PointCloudSquares*` shaders (point topology, no mesh). Dispatch is `width/8 × height/8`, so image dimensions must be divisible by 8. Compute buffers are sized from the **color** camera info and released in `OnDestroy`.

### Robot assets

`Assets/XRViz/Robots/` holds URDF-imported robots (g1, pr2, ur_onrobot, realsense) as prefabs with `ArticulationBody` chains, imported via the Unity URDF-Importer package. The `ur3e_rg2` and other robot prefabs already carry the full joint-state wiring (subscriber on `/joint_states`, state writers, mimic joints) serialized in the prefab — instantiating them is enough.

### Editor tooling (`XRViz` menu)

`Assets/XRViz/Scripts/Editor/` adds menu items that operate on the selected GameObject:
- **XRViz → Add Joint State Components**: wires `RosSubscriberJointState` + state writer components onto a URDF-imported robot and parses the URDF XML to configure mimic joints.
- **XRViz → Fix Missing Meshes**: reassigns missing `MeshFilter`/`MeshCollider` meshes by matching asset name to GameObject name.
- **XRViz → Export ArUco Calibration Tag…**: draws a printable chassis tag from the same code tables the detector reads, so the print and the decoder cannot disagree about the dictionary.
- **XRViz → Check ArUco Detector**: runs the detector and pose solver over synthetic marker images and logs the worst errors — the only way to exercise the calibration maths without a headset.

### MR robot registration

`Assets/XRViz/MRRobotRegistrationTool/` aligns the virtual robot with the physical one: the user places ≥3 points on the real robot, then the ≥3 corresponding points on the virtual model; `RobotRegistrationTool` computes a centroid translation + rotation (from the first two point pairs) and teleports the robot's `ArticulationBody` root.

This is the by-hand alternative to the ArUco calibration above, and is kept because it needs no tag, no printer and no camera permission — and unlike the tag path it works over Quest Link.
