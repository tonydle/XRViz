# XRViz

XR Visualisation Tools for ROS 2 — mixed reality robot visualisation on Meta Quest 3,
built on Unity 6 and Meta XR SDK Building Blocks.

The MVP scene shows a virtual UR3e (with OnRobot RG2 gripper) in passthrough that
mirrors `/joint_states` from ROS 2 live, with a floating ROS connection status panel
and a grab handle to reposition the robot in your room.

## How to run

Full walkthrough with troubleshooting: [Docs/MVP_QUEST3_SETUP.md](Docs/MVP_QUEST3_SETUP.md)

### Prerequisites

- Unity **6000.3.11f1**
- A ROS 2 machine on the same network as the headset/desktop
- Meta Quest 3 (+ **Meta Quest Link** desktop app if running on Windows)

### 1. Unity (one-time)

1. Open the project — the Meta XR SDK packages (`com.meta.xr.sdk.core`,
   `com.meta.xr.sdk.interaction.ovr` 203.0.0) resolve automatically.
2. Run **Meta > Tools > Project Setup Tool → Fix All** for your target
   (Windows and/or Android).
3. Run **XRViz > Create MR MVP Scene (UR3e)** — generates
   `Assets/XRViz/Scenes/MVP_UR3e_MR.unity`, already added to Build Settings. Re-running
   this later only updates the objects it generates and leaves Building Blocks (step 4)
   alone, so step 4 is a one-time setup.
4. From **Meta > Tools > Building Blocks**, add to the scene:
   **[Camera Rig]**, **[Passthrough]**, **[Grab Interaction]** (drives the robot's
   and panel's placement handles), **[Ray Interaction]** (drives the panel's
   Connect/Disconnect buttons — point + trigger, not poke), and optionally
   **[Hand Tracking]**.
5. Set **ROS IP Address** on `Assets/Resources/ROSConnectionPrefab` to your ROS
   machine (port 10000).

### 2. ROS 2

```bash
# TCP endpoint (build ROS-TCP-Endpoint, branch main-ros2, into your workspace first)
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0 -p ROS_TCP_PORT:=10000

# UR3e joint states — real robot:
ros2 launch ur_robot_driver ur_control.launch.py ur_type:=ur3e robot_ip:=<robot-ip> launch_rviz:=false
# …or without hardware:
ros2 launch ur_robot_driver ur_control.launch.py ur_type:=ur3e robot_ip:=0.0.0.0 use_mock_hardware:=true launch_rviz:=false
```

### 3a. Run on Windows desktop (Quest Link — recommended dev loop)

1. In the Meta Quest Link app: set it as the **active OpenXR runtime**
   (Settings > General) and enable **Passthrough over Meta Quest Link**
   (Settings > Beta).
2. Connect the Quest 3 via Link cable or Air Link.
3. Press **Play** in the Unity Editor (or build & run a Windows player).
   - If the Meta XR Simulator is activated, deactivate it first
     (Meta > Meta XR Simulator) — it overrides the Link runtime.

### 3b. Run on the headset (APK)

File > Build Profiles > Android > **Build And Run** with the Quest 3 on USB
(developer mode enabled).

### What you should see

Your room in passthrough, the UR3e at table height in front of you tracking the real
`/joint_states`, and a status panel showing the ROS connection. If something's off,
check the status panel first, then the
[troubleshooting table](Docs/MVP_QUEST3_SETUP.md#troubleshooting).

## Repository tour

- `Assets/XRViz/Scripts/ROS/` — generic `RosPublisher<T>`/`RosSubscriber<T>` bases and
  per-message-type wrappers; `MessageHandling/` connects them to scene behaviour
  (robot joint states, sensors, control, visualisation markers).
- `Assets/XRViz/Robots/` — URDF-imported robots (UR3e/UR5e + OnRobot grippers, PR2,
  Unitree G1, RealSense) as prefabs; the UR prefabs ship fully ROS-wired.
- `Assets/XRViz/Shaders/` — GPU point cloud reconstruction (compute + point shaders).
- `Assets/XRViz/MRRobotRegistrationTool/` — align the virtual robot with the real one
  by placing corresponding points.
- Editor utilities under the **XRViz** menu: scene generation, joint-state wiring for
  new robots, mesh fix-ups.

## License

See [LICENSE](LICENSE).
