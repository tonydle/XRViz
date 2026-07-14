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
   - `Sensor/`: camera images to textures/meshes, IMU, and the GPU point cloud (below).
   - `Control/`: publishing twist/joint commands from XR interactions (e.g. grabbable end effector).
   - `User/`: publishes XR headset/controller tracking to ROS.
   - `Visualizations/`: marker rendering (cubes, line strips).

3. **`Utils/`** — e.g. `CompressedDepthPNGDecoder` for ROS `compressedDepth` (16-bit PNG) payloads.

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
