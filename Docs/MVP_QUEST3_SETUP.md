# XRViz MR MVP — UR3e on Quest 3

Goal: put on a Quest 3, see your room in passthrough with a virtual UR3e that mirrors
`/joint_states` from ROS 2 live, plus a floating ROS connection status panel. The robot
can be repositioned by grabbing a handle under its base.

The stack is built on **Meta XR SDK Building Blocks** (Camera Rig, Passthrough, hand
tracking, grab interactions) on the OpenXR loader — the XRViz layer only adds the ROS
side.

## 1. Unity setup (one-time)

1. Open the project in Unity **6000.3.11f1**. The manifest now pulls in:
   - `com.meta.xr.sdk.core` 203.0.0 (Building Blocks, OVRCameraRig, Passthrough)
   - `com.meta.xr.sdk.interaction.ovr` 203.0.0 (Interaction SDK — grab interactions)
   - `com.meta.xr.simulator` 81.0.1 (optional desktop testing)
2. Switch platform to **Android** (File > Build Profiles) if not already.
3. Run **Meta > Tools > Project Setup Tool** and click **Fix All** (and Apply All
   recommendations) for the Android target. This configures the OpenXR loader features,
   Android manifest entries (passthrough feature flag), IL2CPP/ARM64, etc.
   - The project already uses the OpenXR loader with the Meta XR feature enabled, so
     expect few fixes.
   - If the tool regenerates `Assets/Plugins/Android/AndroidManifest.xml`, verify the
     `android.permission.INTERNET` permission survives — ROS TCP needs it.

## 2. Create the MVP scene

1. Run the menu item **XRViz > Create MR MVP Scene (UR3e)**. This creates and saves
   `Assets/XRViz/Scenes/MVP_UR3e_MR.unity` (added to Build Settings) containing:
   - the `ur3e_rg2` prefab — already wired: `RosSubscriberJointState` on
     `/joint_states` → `RobotStateWriterController` (with RG2 gripper mimic joints) →
     ArticulationBody joints
   - a **Robot Placement Handle** cube (with `RobotPlacementFollower`) under the robot base
   - a world-space **ROS Status Panel** (`RosConnectionStatusUI`) showing IP, connection
     state, and message freshness
   - a directional light
2. Open **Meta > Tools > Building Blocks** and drag into the scene:
   - **[Camera Rig]** (required)
   - **[Passthrough]** (required — this is what makes it mixed reality)
   - **[Hand Tracking]** (optional)
3. Make the handle grabbable with the Interaction SDK: select **Robot Placement
   Handle** and use the Interaction SDK quick action (right-click >
   Interaction SDK > Add Grab Interaction), or drag in the **[Grab Interaction]**
   building block and copy its `Grabbable` setup onto the handle. The
   `RobotPlacementFollower` script teleports the robot to follow the handle
   (yaw-only, so the robot stays upright).
4. Save the scene.

## 3. Point Unity at your ROS machine

Select `Assets/Resources/ROSConnectionPrefab` and set **ROS IP Address** to the machine
that will run the TCP endpoint (currently `192.168.50.250`), port `10000`. Headset and
ROS machine must be on the same network.

## 4. ROS 2 side

ROS-TCP-Endpoint (once):

```bash
cd ~/ros2_ws/src
git clone -b main-ros2 https://github.com/Unity-Technologies/ROS-TCP-Endpoint.git
cd ~/ros2_ws && colcon build && source install/setup.bash
```

Run it:

```bash
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0 -p ROS_TCP_PORT:=10000
```

UR3e joint states — real robot (needs the External Control URCap running):

```bash
ros2 launch ur_robot_driver ur_control.launch.py ur_type:=ur3e robot_ip:=<robot-ip> launch_rviz:=false
```

…or no robot handy (mock hardware still publishes `/joint_states` and accepts
trajectory commands):

```bash
ros2 launch ur_robot_driver ur_control.launch.py ur_type:=ur3e robot_ip:=0.0.0.0 use_mock_hardware:=true launch_rviz:=false
```

Sanity check before putting the headset on:

```bash
ros2 topic echo /joint_states --once
```

## 5. Run it

### Option A — Windows desktop via Quest Link (recommended for development)

Runs on the desktop (Editor Play Mode or a Windows standalone build) with the Quest 3
tethered over Link — no APK, instant iteration.

1. Install the **Meta Quest Link** desktop app on Windows and in its settings set it as
   the **active OpenXR runtime** (Settings > General > OpenXR Runtime).
2. Enable passthrough over Link: Meta Quest Link app > Settings > Beta >
   **Passthrough over Meta Quest Link** (and enable Developer Runtime Features if the
   toggle is hidden).
3. Connect the headset via Link cable or Air Link (Quick Settings > Quest Link on the
   headset).
4. In Unity, run the Project Setup Tool **Fix All** for the Windows/Standalone target
   too. The Standalone target already uses the OpenXR loader with the Meta XR feature,
   Touch Plus controller profile, and hand tracking enabled.
5. With Quest Link active, just press **Play** in the Editor — or build a Windows
   player (File > Build Profiles > Windows) and run the .exe.

Note: the Meta XR Simulator hijacks the OpenXR runtime when activated — make sure it's
**deactivated** (Meta > Meta XR Simulator) when testing over Link.

### Option B — standalone APK on the headset

File > Build Profiles > Android > **Build And Run** with the Quest 3 on USB
(developer mode enabled).

Either way, on launch you should see your room, the UR3e at roughly
table height in front of you, and the status panel to the right.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Status panel shows `connecting…` forever | Wrong ROS IP on `ROSConnectionPrefab`, endpoint not running, or firewall blocking TCP 10000 |
| Connection `ok` but `Joint states: none yet` | Nothing publishing `/joint_states` (driver not running / topic name mismatch) |
| Robot visible but frozen in zero pose | Same as above — joints only move once messages arrive |
| Black surroundings instead of the room | Passthrough block missing, or OVRManager's Passthrough Support not set to Required — re-run the Project Setup Tool |
| Gripper never moves | `/joint_states` doesn't include the RG2 joints (`finger_width`) — run the OnRobot gripper driver; the arm works regardless |

## Notes

- The Meta XR Simulator (Meta > Meta XR Simulator > Activate) lets you smoke-test the
  scene in Play Mode without a headset, minus real passthrough imagery.
- Other robots: the same recipe works for any prefab under `Assets/XRViz/Robots/` —
  wire one with **XRViz > Add Joint State Components** if it isn't already.
