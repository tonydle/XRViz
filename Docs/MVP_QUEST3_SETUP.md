# XRViz MR MVP — UR3e on Quest 3

Goal: put on a Quest 3, see your room in passthrough with a virtual UR3e that mirrors
`/joint_states` from ROS 2 live, plus a floating ROS connection status panel with
Connect/Disconnect buttons. Both the robot and the panel can be repositioned by
grabbing their own handles.

The stack is built on **Meta XR SDK Building Blocks** (Camera Rig, Passthrough, Grab
Interaction, Ray Interaction, optionally Hand Tracking) on the OpenXR loader — the
XRViz layer only adds the ROS side.

## 1. Unity setup (one-time)

1. Open the project in Unity **6000.3.11f1**. The manifest now pulls in:
   - `com.meta.xr.sdk.core` 203.0.0 (Building Blocks, OVRCameraRig, Passthrough)
   - `com.meta.xr.sdk.interaction.ovr` 203.0.0 (Interaction SDK — grab/ray interactions)
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
   - a **Robot Placement Handle** cube (with `RobotPlacementFollower`), positioned at the
     trolley's top-front-left corner, already grab-enabled (see "Moving things around" below)
   - a world-space **ROS Control Panel** group holding three sibling canvases:
     - the **ROS Status Panel** (`RosConnectionStatusUI`) showing IP, connection state, and
       message freshness, with **Connect**/**Disconnect**/**Edit IP**/**Topics** buttons
       (all ray-enabled)
     - the **IP Keypad Panel** popup that **Edit IP** shows/hides
     - the **Topic Browser Panel** popup that **Topics** shows/hides (see
       "Browsing and switching topics" below)

     The group has its own **Panel Placement Handle** (with `PanelPlacementFollower`), also
     already grab-enabled, and `ControlPanelMenuToggle` on the group root hides/shows the
     whole thing with the left controller's Menu button.

     These canvases must stay *siblings* — nesting one Canvas inside another breaks
     ray interaction on the inner one, because `PointableCanvasModule` only accepts
     raycast hits whose `Canvas.rootCanvas` is the exact canvas injected into that
     `PointableCanvas`, and a nested canvas reports its outermost ancestor instead.
   - a directional light

   Re-running this menu item updates the scene in place — it only replaces the objects
   listed above, so Building Blocks and any other manual scene edits are left alone.
   Step 2 below is a one-time setup, not something you redo after every regeneration.

2. Open **Meta > Tools > Building Blocks** and add every block below to the scene, once.
   The handles and panel buttons the generator creates are already wired to use whatever
   interactors these blocks add, they just don't work until the blocks exist:
   - **[Camera Rig]** — required, root of everything else below
   - **[Passthrough]** — required, this is what makes it mixed reality
   - **[Grab Interaction]** — required, drives *near* grab on both Robot Placement Handle
     and Panel Placement Handle
   - **[Ray Interaction]** — required, drives the ROS Status Panel's and IP Keypad Panel's
     buttons (point + pull the trigger, like a normal menu — not poke/touch), and *ray*
     grab on both placement handles
   - **[Hand Tracking]** — optional, lets the above work with bare hands instead of
     controllers
3. Save the scene.

## Browsing and switching topics

**Topics** on the status panel opens the Topic Browser, which asks the endpoint what it is
advertising and lets you re-point the joint-state subscriber at a different topic without
taking the headset off. Useful when `/joint_states` is silent and you need to find out
whether the driver is publishing somewhere else (a namespace, a bag remap, `/robot/joint_states`).

How it works:

- The list comes from `ROSConnection.GetTopicAndTypeList()`, which sends the `__topic_list`
  system command to `ros_tcp_endpoint`. The reply is dispatched from `ROSConnection.Update()`,
  so `TopicBrowserUI` handles it on the main thread and writes straight to the UI.
- **It is filtered by message type.** Only topics whose type matches the subscriber's
  (`sensor_msgs/JointState`) are listed; the header shows `3 of 47 topics match`. Subscribing
  to a mismatched type just produces deserialization errors, so the filter is on by default —
  untick `Show All Types` on the component to see everything.
- Picking a row calls `IRosTopicBinding.SetTopic`, which unsubscribes from the old topic and
  subscribes to the new one live. The current topic is marked ▶ in the list.
- The list is a **snapshot**, not a subscription — it refreshes when the panel is opened and
  when you press **Refresh**.
- `GetTopicAndTypeList` has no failure path: if the endpoint isn't connected, the request
  silently goes nowhere and the callback never fires. `TopicBrowserUI` therefore runs its own
  5 s deadline and shows *no response - is ROS connected?* rather than hanging on "requesting…".

Paged with **Prev**/**Next** (8 rows a page) rather than scrolled — a `ScrollRect` is fussy to
drag accurately with a ray, and fixed rows need no viewport mask or layout group.

To point a *different* subscriber at a topic this way, have it extend `RosSubscriber<T>`
(which implements `IRosTopicBinding`) and assign it to the browser's `_target`.

## Moving things around

Both placement handles get two interactables wired up by the generator, feeding one shared
`Grabbable` that moves the handle (the robot / panel then follows via
`RobotPlacementFollower` / `PanelPlacementFollower`):

| Child object | Interactable | How you use it | Needs |
| --- | --- | --- | --- |
| `Grab Interaction` | `HandGrabInteractable` + `GrabInteractable` | reach out and grab the cube | [Grab Interaction] |
| `Ray Grab Interaction` | `RayInteractable` + `ColliderSurface` | point at the cube from across the room, hold the trigger, drag | [Ray Interaction] |

Ray grab is what makes the robot and panel repositionable without walking over to them.

The panel is moved by its handle rather than being grabbable itself on purpose: its canvases
already carry `RayInteractable`s for the buttons, so a second ray-grab surface over the same
area would fight with button clicks for the ray.

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

### Option B — standalone APK on the headset

File > Build Profiles > Android > **Build And Run** with the Quest 3 on USB
(developer mode enabled).

**Before the first Android build, run XRViz > Bake URDF Joint Names on each robot**
(select the robot root, then save the prefab). See "URDF Importer is desktop-only" below
for why — without it the robot builds fine but never moves in the APK.

Either way, on launch you should see your room, the UR3e at roughly
table height in front of you, and the status panel to the right.

### URDF Importer is desktop-only

`com.unity.robotics.urdf-importer`'s **runtime** assembly is restricted to
Editor/Win64/Linux64/macOS in its asmdef, because it ships native AssimpNet and VHACD
binaries with no Android build:

```json
"includePlatforms": ["Editor", "LinuxStandalone64", "macOSStandalone", "WindowsStandalone64"]
```

So anything under `Assets/` that names a `UrdfImporter` type fails to compile the moment you
switch to the Android platform (`error CS0246: The type or namespace name 'UrdfImporter' could
not be found`) — even though it compiles fine in the Editor and for Quest Link. Adding
`"Android"` to that list is not a fix: the native libraries still don't exist, and the package
lives in `Library/PackageCache` so the edit is wiped on reimport.

XRViz runtime scripts therefore avoid the importer entirely:

- **Joint names** come from a baked `UrdfJointName` component (plain `Assembly-CSharp`) rather
  than `UrdfJoint.jointName`. **XRViz > Bake URDF Joint Names** copies them across; it also
  runs automatically as step 6 of **XRViz > Add Joint State Components**. `JointStateWriter`
  still falls back to reading `UrdfJoint` in the Editor and on desktop, logging a warning, so
  an un-baked robot keeps working in the Quest Link loop instead of silently breaking.
- **Collision meshes** are found via the child GameObject named `Collisions` rather than the
  `UrdfCollisions` component. The importer always uses that exact name.

The `UrdfJoint` / `UrdfCollisions` / `UrdfLink` components serialized in the robot prefabs
become missing scripts in an Android player. That is expected and harmless — it logs
"referenced script is missing" warnings, while `ArticulationBody`, meshes, and renderers
(everything the robot actually needs at runtime) are core Unity types and are unaffected.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Status panel shows `connecting…` forever | Wrong ROS IP on `ROSConnectionPrefab`, endpoint not running, or firewall blocking TCP 10000 |
| Connection `ok` but `Joint states: none yet` | Nothing publishing `/joint_states` (driver not running / topic name mismatch) — open **Topics** on the panel to see what is actually being advertised, and switch to it |
| Topic Browser stuck on `requesting…` then `no response` | Not connected to the endpoint — the topic-list request has no error path, so this is the browser's own 5 s deadline firing |
| Topic Browser says `none of N topics match` | Nothing is publishing the subscriber's message type; untick `Show All Types` on `TopicBrowserUI` to see the full list |
| Robot visible but frozen in zero pose | Same as above — joints only move once messages arrive |
| Black surroundings instead of the room | Passthrough block missing, or OVRManager's Passthrough Support not set to Required — re-run the Project Setup Tool |
| Gripper never moves | `/joint_states` doesn't include the RG2 joints (`finger_width`) — run the OnRobot gripper driver; the arm works regardless |
| Robot moves over Quest Link but is frozen in the APK | Joint names not baked — run **XRViz > Bake URDF Joint Names** on the robot and save the prefab |
| `CS0246: 'UrdfImporter' could not be found` after switching to Android | A runtime script under `Assets/` references the desktop-only URDF Importer assembly — see "URDF Importer is desktop-only" |

## Notes

- Other robots: the same recipe works for any prefab under `Assets/XRViz/Robots/` —
  wire one with **XRViz > Add Joint State Components** if it isn't already.
