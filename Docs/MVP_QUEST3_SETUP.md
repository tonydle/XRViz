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
   - an amber **Robot Placement Handle** sphere (with `PlacementHandle`), positioned at the
     trolley's top-front-left corner, already grab-enabled (see "Moving things around" below)
   - a **Laser Scan** object (`RosSubscriberLaserScan` + `LaserScanVisualizer`, no default
     topic — pick one from the Topics browser) with its own magenta **Laser Scan Placement Handle**
   - a **Point Cloud** object (`DepthImagePointCloud` + colour/depth `RosSubscriberImage` and a
     `RosSubscriberCameraInfo` on named children) with a green **Point Cloud Placement Handle**
     that *is* the cloud's origin — see "RGBD point cloud" below
   - a world-space **ROS Control Panel** group holding three sibling canvases:
     - the **ROS Status Panel** (`RosConnectionStatusUI` + `ControlPanelActions`) showing IP,
       connection state, and message freshness, a key for the handle colours, and
       **Connect**/**Disconnect**/**Edit IP**/**Topics**/**Clear Scan**/**Reset Anchors**
       buttons (all ray-enabled)
     - the **IP Keypad Panel** popup that **Edit IP** shows/hides
     - the **Topic Browser Panel** popup that **Topics** shows/hides (see
       "Browsing and switching topics" below)

     The group has its own cyan **Panel Placement Handle** (with `PlacementHandle`), also
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
   - **[Grab Interaction]** — required, drives *near* grab on every placement handle
   - **[Ray Interaction]** — required, drives the ROS Status Panel's, IP Keypad Panel's and
     Topic Browser Panel's buttons (point + pull the trigger, like a normal menu — not
     poke/touch), and *ray* grab on every placement handle
   - **[Hand Tracking]** — optional, lets the above work with bare hands instead of
     controllers
3. Save the scene.

## The ROS control panel

Three sibling world-space canvases under one group root. The status panel is always there; the
keypad and topic browser are popups toggled from it.

| Button | Does |
| --- | --- |
| **Connect** / **Disconnect** | the ROS TCP connection (`RosConnectionStatusUI`) |
| **Edit IP** | shows/hides the numeric keypad — no native VR keyboard is installed to hook a `TMP_InputField` to |
| **Topics** | shows/hides the topic browser (below) |
| **Clear Data** | wipes every visualisation holding geometry — laser scan, point cloud, anything implementing `IClearableVisualization` |
| **Reset Anchors** | puts every placement handle back where it started (see "Putting them back") |
| **Anchor: TF** / **Anchor: Manual** | switches every visualisation between `/tf` placement and hand placement (see "Placing from TF"). The label says what pressing it *gets* you |
| **TF Anchors** | shows/hides the per-visualisation TF panel |

Below the status readout is the **anchor key**: a coloured dot per placement handle, generated
from the same `k_HandleStyles` table as the handles themselves.

`ControlPanelActions` owns the two scene-wide buttons and shows a one-line confirmation under
them for a couple of seconds ("cleared 2 visualisations", "reset 4 anchors"). Without it, pressing
**Clear Data** when there is nothing to clear is indistinguishable from a dead button — which, on
a ray-driven UI where a near-miss produces exactly nothing, is a real failure mode.

**Clear Data** finds implementors of `IClearableVisualization` rather than holding a serialized
list, so a visualisation added later is picked up with no wiring. Unity cannot search for an
interface directly, hence the sweep over `MonoBehaviour`s — irrelevant at the rate a button is
pressed.

### Styling

Every panel goes through `StylePanel`, which gives it a near-opaque dark background and a
titled header bar. **The background is not decoration.** These canvases float over passthrough
video of a real room; against a white wall or a window, light text on a translucent panel simply
cannot be read. Buttons are colour-coded by consequence (green connect, red disconnect, blue
navigation, grey neutral) with hover and press states derived from the base colour, so a ray
resting on a button is visible before the trigger is pulled.

The sprites are Unity's own built-ins — `UI/Skin/Background.psd`, `UISprite.psd` and `Knob.psd`,
fetched with `AssetDatabase.GetBuiltinExtraResource`. They 9-slice with rounded corners, which is
the whole difference between "a panel" and "a rectangle of flat colour", and they need no art
added to the project. `Knob.psd` is the only round built-in, so it stands in for the spheres in
the anchor key.

Panel geometry is authored in UI units and scaled to metres by `k_PanelUnitsToMeters` (0.001), so
the 420 × 500 status panel is 42 × 50 cm in the room. If you resize it, move the Panel Placement
Handle's `_offset` to match — it has to clear the panel's half-height.

## Browsing and switching topics

**Topics** on the status panel opens the Topic Browser, which asks the endpoint what it is
advertising and lets you re-point the joint-state subscriber at a different topic without
taking the headset off. Useful when `/joint_states` is silent and you need to find out
whether the driver is publishing somewhere else (a namespace, a bag remap, `/robot/joint_states`).

How it works:

- The list comes from `ROSConnection.GetTopicAndTypeList()`, which sends the `__topic_list`
  system command to `ros_tcp_endpoint`. The reply is dispatched from `ROSConnection.Update()`,
  so `TopicBrowserUI` handles it on the main thread and writes straight to the UI.
- **◀ ▶ at the top picks which subscriber you are retargeting** — joint states or laser scan in
  the generated scene. The label shows its message type and current topic.
- **It is filtered by that target's message type.** Only matching topics are listed; the status
  line shows `3 of 47 topics match`. Subscribing to a mismatched type just produces
  deserialization errors, so the filter is on by default — **tick** `Show All Types` on the
  component to bypass it and list every advertised topic. Switching target re-filters the cached
  reply instantly rather than costing another round trip.
- **ROS 1 vs ROS 2 type names are normalised before comparing.** ROS 1 calls a type
  `sensor_msgs/LaserScan`; ROS 2 calls it `sensor_msgs/msg/LaserScan`, and `ros_tcp_endpoint`
  passes whichever it sees straight through. Unity's `MessageRegistry` only ever knows the
  ROS 1 form, so `TopicBrowserUI` strips the `/msg/` infix from both sides — without that, a
  ROS 2 endpoint matches *nothing* and the list comes back silently empty.
- When nothing matches, the browser logs every advertised topic and its exact type string to
  the console (visible in the Editor, or over `adb logcat` on the headset). That is the fastest
  way to see what the endpoint really thinks your topic's type is.
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
(which implements `IRosTopicBinding`) and add it to the browser's `_targets` array.

## Visualising sensor topics

### Laser scan

`RosSubscriberLaserScan` converts the polar `ranges` array into Unity-space points in the
sensor's own frame; `LaserScanVisualizer` draws them as small cubes.

Two decisions worth knowing about, because they differ from the older point-cloud code:

- **Coordinates go through `FLU.ConvertToRUF`**, the connector's own conversion
  (`Unity.Robotics.ROSTCPConnector.ROSGeometry`). ROS is right-handed Z-up, Unity left-handed
  Y-up; a scan copied straight across lands rotated and mirrored. Never hand-roll this —
  `FLU.ConvertToRUF(v) => new Vector3(-v.y, v.z, v.x)` is the canonical version.
- **The scan is a Mesh on a MeshFilter, not `Graphics.DrawProcedural`.** DrawProcedural renders
  in world space and ignores the GameObject's Transform, so a `PlacementHandle` cannot move it.
  A mesh under a MeshRenderer is transformed by Unity for free. (`PointCloudRosGPU_PointCloud2`
  still has the DrawProcedural problem — it is not handle-movable yet.)

Cubes rather than points or flat quads: `MeshTopology.Points` draws single pixels, and quads
lying in the scan plane vanish edge-on — which is exactly where your head is relative to a
floor-level lidar. Invalid beams (NaN, ±Inf, or outside `[range_min, range_max]`) are dropped
rather than drawn at the origin. Above `_maxPoints` (4096) the scan is decimated by stride.

Colour is a near→far gradient over the scan's range limits, baked into `Mesh.colors` and drawn
with `Shaders/VertexColorUnlit.shader`. It has to be that shader, or one like it — the built-in
`Unlit/Color` ignores vertex colours and the gradient would render flat. Unlit is deliberate:
passthrough MR has no useful scene lighting, so a lit shader renders visualisations near-black.
That shader also carries the `UNITY_VERTEX_OUTPUT_STEREO` macros Quest needs under single-pass
instanced rendering; without them a mesh draws to one eye only.

#### Clearing a stale scan

A drawn scan is a mesh — it stays on screen after the data stops, and a frozen sweep looks
exactly like a live one. Two things deal with that:

- **It expires on its own.** `LaserScanVisualizer._staleAfterSeconds` (3 s by default) blanks the
  mesh when nothing has arrived for that long. Set it to 0 to keep the last sweep indefinitely.
  The clock is `Time.realtimeSinceStartup` at *arrival*, not the message header stamp — the
  header is the sensor's clock, which need not agree with Unity's.
- **Clear Scan** on the status panel wipes every `LaserScanVisualizer` in the scene immediately.

Both go through `LaserScanVisualizer.Clear()`, which also calls `RosSubscriberLaserScan.ClearData()`.
Clearing only the mesh would not be enough — the visualiser rebuilds it every frame from the
subscriber's last parsed message, so the sweep would reappear on the next frame.

### RGBD point cloud

`DepthImagePointCloud` reconstructs a cloud from an RGBD camera: a depth image, a colour image,
and the depth camera's `CameraInfo`. Reconstruction runs on the GPU
(`Shaders/DepthImagePointCloudGPU.compute`), and the result is drawn as camera-facing squares
with `Shaders/PointCloudBillboard.shader`.

Generated child objects — none start subscribed to anything:

| Child object | Type |
| --- | --- |
| `Color Image` | `sensor_msgs/Image` |
| `Depth Image` | `sensor_msgs/Image` |
| `Depth Camera Info` | `sensor_msgs/CameraInfo` |

All three are targeted from the Topics browser, which only ever lists topics the endpoint is
actually advertising — they appear in its ◀ ▶ list by GameObject name, which is why the two
`sensor_msgs/Image` subscribers live on named children rather than stacked on the cloud root.
The point cloud has nothing to draw until all three are picked.

#### The GameObject is the origin

This is the point of the thing. The visualiser hands its own `localToWorldMatrix` to the compute
shader as `originTransform`, and every point is projected out of it: the camera's optical centre
sits exactly on the GameObject and the cloud extends along its **+Z**. Park the green handle
where the real camera stands in the room and the virtual geometry lands on the real geometry.

It is the one handle generated with `_yawOnly` **off**, because a camera has to be aimed and
flattening its rotation to yaw would throw the pitch away. That has a consequence worth knowing:
a handle whose target keeps a non-identity rotation must *start* at that rotation, or the first
grab snaps the target to identity. `CreatePlacementHandle` takes a `rotation` argument for this,
and the handle's `_offset` is expressed in the rotated space so it keeps sitting below the origin
as the cloud is turned.

#### Raw `sensor_msgs/Image`, not `CompressedImage`

`RosSubscriberImage` decodes raw images, choosing the texture format from the message's own
`encoding` field: `rgb8`, `bgr8`, `rgba8`, `bgra8`, `mono8`/`8UC1`, `mono16`/`16UC1`, `32FC1`.

Raw because that is what a simulator emits natively — the compressed variants only exist if you
additionally run `image_transport` republishers — and because it keeps the depth exact.
`compressedDepth` quantises to a 16-bit PNG, whereas a sim's `32FC1` depth is already metric
float.

`GetDepthMetersPerUnit()` reports what a texel must be multiplied by to get metres, so the
consumer never has to know which it got:

| Encoding | Texture format | Factor | Why |
| --- | --- | --- | --- |
| `32FC1` | `RFloat` | 1 | samples the stored value; already metres |
| `16UC1` | `R16` | 65535 × 0.001 | R16 is UNorm so sampling gives [0,1]; uint16 depth is millimetres |

Two upload details that are easy to get wrong:

- **`step` may carry row padding.** It is the full row length in bytes and is allowed to exceed
  `width × bytes-per-pixel`, while `LoadRawTextureData` wants it tightly packed. Padded frames are
  repacked into an exactly-sized scratch buffer, kept between messages so it doesn't allocate a
  megabyte per frame.
- **No vertical flip is applied, deliberately.** ROS image data starts top-left, Unity's
  `LoadRawTextureData` fills from bottom-left, and the two cancel: texel `(x, y)` *is* ROS pixel
  `(x, y)`, which is what keeps the pixel coordinates agreeing with the camera intrinsics. The
  texture therefore renders upside down if you put it straight on a quad — flip it there.

#### Optical frame, not FLU

The frame convention here is the one thing most likely to bite. A camera's `*_optical_frame` is
**not** the FLU body frame that `FLU.ConvertToRUF` converts from. Per REP 103/145 — and the
comment in `sensor_msgs/Image` itself — an optical frame is right-down-forward: +x right across
the image, +y down, +z into the scene.

Unity is right-up-forward, so the conversion is a plain Y flip:

```hlsl
float3 local = float3(xRight, -yDown, depth);   // NOT FLU.ConvertToRUF
```

Applying the FLU conversion instead lands the cloud on its side. (The older
`PointCloudReconstructionGPU.compute` treats the camera frame as FLU, which is why its clouds
come out rotated.)

#### Rendering

Six vertices per point, expanded in the *vertex* shader from `SV_VertexID` and issued as
`MeshTopology.Triangles`. Not a geometry shader, and not `PointCloudSquares.shader`, which fails
on Quest twice over: it has no `UNITY_VERTEX_OUTPUT_STEREO` so it draws to one eye under
single-pass instanced rendering, and Adreno has no native geometry stage — the driver emulates it,
which on a 300k-point cloud is the whole frame budget.

Points with no valid depth are marked alpha 0 by the compute shader and pushed outside the clip
volume by the vertex shader, so they're discarded before rasterisation rather than costing fill.

`Graphics.DrawProcedural` is used rather than a mesh. That normally breaks handle movement — it
renders in world space and ignores the Transform — but here the Transform is already baked into
the positions by the compute shader, which is the *reason* the cloud is origin-anchored. The
bounds passed to the draw are recentred on the transform each frame so it isn't frustum-culled
after being moved.

#### Bandwidth is the real limit

Not the GPU. Raw 640×480 `rgb8` is 900 KB a frame and 640×480 `32FC1` depth is 1.2 MB; at 30 Hz
that is roughly **60 MB/s over a TCP socket to a headset on wifi**, which will not work.

Throttle on the ROS side — a lower publish rate, or a smaller camera resolution — before blaming
Unity. `_decimation` (default 2, so a quarter of the points) thins what gets *drawn* and is worth
tuning for frame rate, but the bytes have already crossed the network by the time it applies.

Other tunables: `_pointSize` (metres, default 12 mm), `_minRange`/`_maxRange` (metres, also the
cull for NaN and no-return depth), `_staleAfterSeconds`, and `_drawWithoutColor` — which draws
flat grey from depth alone when no colour image has arrived, so "no depth" and "no colour" don't
look identical while bringing a camera up.

## Moving things around

`PlacementHandle` is the one generic handle — a grabbable sphere that repositions whatever it is
pointed at. Assign `_target` for a plain Transform (panel, laser scan) or
`_targetArticulationBody` for a robot, whose root ignores writes to its Transform and has to be
moved with `TeleportRoot`. `_yawOnly` keeps the target upright; turn it off to aim something.

### Which handle is which

Five spheres, ~3–5 cm, colour-coded, with a matching key on the status panel:

| Handle | Colour | Moves |
| --- | --- | --- |
| Robot Placement Handle | amber | the UR3e (via `ArticulationBody.TeleportRoot`) |
| Panel Placement Handle | cyan | the whole ROS Control Panel group |
| Laser Scan Placement Handle | magenta | the laser scan visualisation |
| Point Cloud Placement Handle | green | the RGBD cloud's **origin** — see "RGBD point cloud" |
| TF Origin Placement Handle | white, a size up | the **fixed frame** — the origin of the whole TF world |

Spheres rather than cubes because a cube's silhouette changes with viewing angle — at this size
it reads as a different object depending on where you stand. Amber/cyan/magenta rather than
red/green/blue so the three stay distinguishable with the common colour deficiencies, and away
from the greys and skin tones that fill a passthrough view.

The colours live in one place, `k_HandleStyles` in `XRVizCreateMVPScene.cs`, which drives both
the handle materials and the panel's key — so they cannot drift apart. Each handle gets a
generated material in `Assets/XRViz/Materials/`, using `Shaders/HandleUnlit.shader`. It has to be
a material *asset*: a `new Material(...)` created by an editor script is not saved anywhere the
player build can find, so the handles would come out untinted in the APK.

`HandleUnlit` takes no scene lighting — a handle must be equally findable against a dark bench
and a sunlit wall — but bakes in a fixed top-lit ramp and a rim term, without which a flat unlit
sphere reads as a paper disc in stereo. It carries the `UNITY_VERTEX_OUTPUT_STEREO` macros, so it
draws to both eyes.

### Grabbing

Each handle gets two interactables wired up by the generator, feeding one shared `Grabbable`:

| Child object | Interactable | How you use it | Needs |
| --- | --- | --- | --- |
| `Grab Interaction` | `HandGrabInteractable` + `GrabInteractable` | reach out and grab the sphere | [Grab Interaction] |
| `Ray Grab Interaction` | `RayInteractable` + `ColliderSurface` | point at the sphere from across the room, hold the trigger, drag | [Ray Interaction] |

Ray grab is what makes things repositionable without walking over to them.

The panel is moved by its handle rather than being grabbable itself on purpose: its canvases
already carry `RayInteractable`s for the buttons, so a second ray-grab surface over the same
area would fight with button clicks for the ray. The same reasoning applies to any visualisation
you want both clickable and movable — give it a handle.

### Putting them back

**Reset Anchors** on the status panel returns every `PlacementHandle` in the scene, and whatever
it carries, to where it started. Ray grab makes it very easy to fling something behind you or
through a wall, and this is the way back without regenerating the scene.

Two things to know:

- **The default pose is captured at `Awake`, not serialized.** A handle you nudge in the Editor
  before pressing Play resets to where you left it, not to whatever the generator last wrote.
- **The control panel moves too** — its own handle is one of the anchors. That is intended; the
  panel is as easy to lose as anything else.

`ControlPanelActions` finds its targets with `FindObjectsByType` at press time rather than
holding a serialized list, so a handle you add to the scene by hand is picked up as well.

## Placing from TF

Hand placement answers "where in this room do I want to see this?". TF placement answers "where
is this, according to the robot?". Both are useful at different points of bringing a rig up, so
each visualisation can be switched between them at runtime — **Anchor: TF** on the status panel
does all of them, the **TF Anchors** panel does one row at a time.

### The one thing you align

While TF anchoring is on, every anchored visualisation is placed from `/tf`, so their positions
relative to *each other* come from the robot and are no longer yours to set. Their handles hide,
because TF owns those poses now and a grabbable that does nothing reads as a broken app.

What stays is the **white TF origin handle**: `RosTfTree`'s own Transform is the fixed frame, and
every TF pose is measured out from it. Park it on the real robot's base and the laser lands at the
laser's frame, the cloud at the camera's, the model on the real arm. It is the single alignment
that places everything else — which is the entire point of the mode.

The frame itself is drawn as a 15 cm **axis triad** (red/green/blue = ROS x/y/z, RViz's colours,
along the ROS axes rather than Unity's) because that is the thing you are actually aligning — the
handle is a grip and sits a metre above it, on the opposite trolley corner from the robot's amber
one. Line the triad up with the real base, not the sphere.

Switch back to **Anchor: Manual** and each handle reappears where TF left its object
(`PlacementHandle.SnapToTarget`), so nothing jumps and you carry on from there.

### Where the frames come from

Each anchor takes its frame from its topic's own `header.frame_id`, via `IRosFrameSource` on the
subscriber — so retargeting a topic in the browser retargets its anchor too, with nothing to
retype (there is no keyboard in the headset). The exception is the robot: no message carries a
robot's root frame name, so `TfAnchor._frameId` is typed in the Inspector, defaulting to
`base_link`.

The **TF Anchors** panel shows each anchor's frame and what it is doing:

| Row says | Means |
| --- | --- |
| `TF` | placed from `/tf` this frame |
| `manual` | its handle owns the pose |
| `no msg` | nothing has arrived on that topic yet, so its frame is unknown |
| `not in /tf` | the frame is known but the TF tree has never heard of it |
| `no TF origin` | there is no `RosTfTree` in the scene |

A frame that cannot be resolved leaves the object **where it is**. That is deliberate: placing it
at the origin instead would look exactly like a measurement, and a cloud confidently in the wrong
place is worse than one you can see hasn't moved.

### Gotchas

- **`/tf_static` is latched, and we subscribe late.** `ros_tcp_endpoint` only subscribes when this
  scene connects, so static transforms published before that are simply missed and their frames
  never appear. If a static link is stuck on `not in /tf`, restart (or republish from) the node
  that owns it *after* connecting.
- **Both TF topics share one table.** The package's own `TFSystem` keeps a separate table per
  topic, so a chain crossing `/tf` and `/tf_static` — a fixed sensor mount under a moving arm,
  i.e. the normal case — cannot be resolved at all. `RosTfTree` exists for that reason; don't
  swap it back.
- **Frames from different trees are refused.** If the fixed frame and the data's frame have no
  common root, there is no known relationship between them and the anchor reports failure rather
  than composing them anyway.
- **Optical frames get a rotation, not a flip.** `*_optical_frame` is right-down-forward while TF
  transforms are converted as FLU, and the two differ by an axis permutation. `TfAnchor` applies
  `k_OpticalCorrection` (−120° about `(1,1,1)`) automatically on any frame with that suffix. If a
  cloud comes out on its side, check that first: a forward-looking camera must resolve to
  *identity* rotation relative to its parent link.

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
| Topic Browser says `0 of N match <Type>` | Check the ◀ ▶ target selector is on the subscriber you mean — the list only shows topics matching *that* target's type. Then read the console dump the browser logs, which names every advertised topic and its exact type string. Tick `Show All Types` to bypass the filter entirely |
| A topic you know is publishing never appears | First rule out a type-name mismatch (the browser normalises ROS 2's `sensor_msgs/msg/X` to `sensor_msgs/X`, but a custom or remapped type may still not match). If the type is right, the topic is not in the **endpoint's** view of the ROS graph — run `ros2 topic list` *on the machine running `ros_tcp_endpoint`*, not on the machine publishing. A topic missing there is a ROS-side discovery problem (different `ROS_DOMAIN_ID`, different `RMW_IMPLEMENTATION`, or DDS multicast blocked between hosts) and nothing in Unity can fix it |
| A topic appears in the browser that nothing publishes | Expected. Unity subscribing to a topic creates a ROS subscription, which puts that topic in the graph with zero publishers — so the generated default shows up whether or not a scanner exists |
| ROS connection drops every few seconds, endpoint logs `AttributeError: 'NoneType' object has no attribute 'msg'` in `tcp_sender.py` `send_topic_list` | Known `ros_tcp_endpoint` bug — the raise kills the client thread and the TCP connection with it, so **nothing** receives data, not just the topic list. Untick `Refresh On Open` on `TopicBrowserUI` as a stopgap and apply the patch in `Docs/ROS_ENDPOINT_TOPIC_LIST_FIX.md` |
| Robot visible but frozen in zero pose | Same as above — joints only move once messages arrive |
| Black surroundings instead of the room | Passthrough block missing, or OVRManager's Passthrough Support not set to Required — re-run the Project Setup Tool |
| Gripper never moves | `/joint_states` doesn't include the RG2 joints (`finger_width`) — run the OnRobot gripper driver; the arm works regardless |
| Robot moves over Quest Link but is frozen in the APK | Joint names not baked — run **XRViz > Bake URDF Joint Names** on the robot and save the prefab |
| `CS0246: 'UrdfImporter' could not be found` after switching to Android | A runtime script under `Assets/` references the desktop-only URDF Importer assembly — see "URDF Importer is desktop-only" |

## Notes

- Other robots: the same recipe works for any prefab under `Assets/XRViz/Robots/` —
  wire one with **XRViz > Add Joint State Components** if it isn't already.
