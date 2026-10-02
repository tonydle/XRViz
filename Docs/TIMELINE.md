# XRViz — Development Timeline

A running record of what has been built, in order, and *why* the non-obvious decisions were
made. Companion to `Docs/MVP_QUEST3_SETUP.md` (which is the how-to) and `CLAUDE.md` (which is
the architecture summary).

Branch: `camdev`. Dates are commit dates.

---

## At a glance

| Date | Commit | Author | Theme |
|---|---|---|---|
| 2026-04-24 | `39c4877` | Tony Le | Initial commit |
| 2026-04-30 | `b7adbbb` | Tony Le | Project scaffolding |
| 2026-05-01 | `e1b8cdd` | Tony Le | GPU point cloud (depth-image reconstruction) working |
| 2026-07-14 | `51c91ca` | Cameron Johns | Restart on `camdev` — minimum viable XRViz |
| 2026-08-06 | `238d7c3` | Cameron Johns | MVP scene, ROS panel controls, UR3 transform + UI grab |
| 2026-08-07 | `c0ec973` | Cameron Johns | Scene generated from script instead of hand-built |
| 2026-08-07 | `6951ab6` | Cameron Johns | Android build fixes, ray grab, dynamic topic subscription |
| *uncommitted* | — | Cameron Johns | Generic placement handle + laser scan visualisation |
| *uncommitted* | — | Cameron Johns | Handle colour-coding, panel styling, clear/reset actions |
| *uncommitted* | — | Cameron Johns | RGBD point cloud anchored to a movable origin |

---

## Phase 1 — Inherited foundation (Apr–May 2026)

Original project by Tony Le. Established the Unity + ROS 2 base: the ROS-TCP-Connector
integration, the `RosPublisher<T>` / `RosSubscriber<T>` generic bases, URDF-imported robot
prefabs, and the GPU point cloud pipeline.

The point cloud here is **depth-image reconstruction** — paired colour + depth
`CompressedImage` topics with their `CameraInfo`, pushed through
`Shaders/PointCloudReconstructionGPU.compute` and drawn with `Graphics.DrawProcedural`. Not
`sensor_msgs/PointCloud2`; that came later and separately.

## Phase 2 — Restart on `camdev` (14 Jul 2026)

`51c91ca` — software suite restarted and published to a new branch. Minimum viable XRViz:
passthrough plus a simple UR3 model driven by a `/joint_states` subscriber.

## Phase 3 — MVP scene and XR interaction (6 Aug 2026)

`238d7c3` — first working mixed-reality scene, `MVP_UR3e_MR.unity`.

- Meta XR SDK **Building Blocks** adopted as the way to assemble XR scenes (Camera Rig,
  Passthrough, Grab/Ray Interaction) rather than AR Foundation or XRI rigs.
- `RosConnectionStatusUI` — world-space panel showing IP, connection state, and message
  freshness, with Connect/Disconnect.
- `ControlPanelMenuToggle` — panel hides by default, toggled with the left controller's Menu
  button, so it doesn't float in view permanently.
- `RobotPlacementFollower` and `PanelPlacementFollower` — grab handles that reposition the
  robot and the panel.

## Phase 4 — Scene generated from script (7 Aug 2026)

`c0ec973` — the scene stopped being a hand-built artefact and became the output of
**XRViz → Create MR MVP Scene (UR3e)**, mirroring how Meta's Building Blocks work.

The generator updates the scene *in place* rather than rebuilding from empty, specifically so
that Building Blocks and any manual scene edits survive a re-run — Building Blocks aren't
scriptable the way the rest of the scene is, and re-adding them by hand every time would make
regeneration useless.

Also added `IpKeypadUI`: a numeric keypad popup for retyping the ROS IP at runtime, because no
native VR keyboard is installed in this project's packages to hook a `TMP_InputField` up to.

## Phase 5 — Android build fixes, ray grab, topic browser (7 Aug 2026)

`6951ab6`. Several independent problems, found and fixed in sequence.

### The number pad was dead to ray interaction

The status panel's buttons worked; the keypad's did not. Cause was in the Interaction SDK:

```csharp
// PointableCanvasModule.FindFirstRaycastWithinCanvas
candidateCanvas = candidateGameObject.GetComponentInParent<Canvas>();
if (candidateCanvas.rootCanvas != canvas) continue;   // discards every keypad hit
```

The keypad Canvas had been created as a **child** of the status panel Canvas. Unity's
`rootCanvas` resolves to the outermost ancestor Canvas, so a keypad button reported the *status
panel* canvas — never equal to the keypad canvas injected into its `PointableCanvas`. Every hit
was thrown away.

**Fix:** a `ROS Control Panel` group root (a plain Transform) with the canvases as *siblings*,
so each is its own `rootCanvas`. This is now a standing rule — see `MVP_QUEST3_SETUP.md`.

### The robot placement handle had no grab components at all

The generator's comment claimed the handle was grab-enabled, but the `AddGrabInteraction` call
was never written. Confirmed against the serialised scene: the robot handle had 5 components
against the panel handle's 7, and the whole scene contained exactly one `Grab Interaction`
child instead of two. The robot could never be moved.

**Fix:** the missing call, plus **ray grab** (`ISDK_RayGrabInteraction` + a `ColliderSurface`
wrapping the handle's collider) added alongside near grab on every handle — so handles can be
dragged from across the room instead of requiring you to walk over and touch an 8 cm cube.

### `CS0246: 'UrdfImporter' could not be found` when targeting Android

The URDF Importer's **runtime** assembly is desktop-only by design:

```json
"includePlatforms": ["Editor", "LinuxStandalone64", "macOSStandalone", "WindowsStandalone64"]
```

It ships native AssimpNet and VHACD binaries with no Android build. Code compiled fine in the
Editor and for Quest Link, then failed the moment the platform switched. Adding `"Android"` to
that list is not a fix — the native libraries still don't exist, and the package lives in
`Library/PackageCache` so the edit is wiped on reimport.

**Fix:** XRViz runtime scripts no longer name any `UrdfImporter` type.

- Joint names now come from a baked `UrdfJointName` component (plain `Assembly-CSharp`), written
  by the new **XRViz → Bake URDF Joint Names** menu item. `JointStateWriter` still falls back to
  reading `UrdfJoint` in the Editor and on desktop — with a warning — so an un-baked robot keeps
  working in the Quest Link loop instead of silently breaking.
- Collision meshes are found via the child GameObject named `Collisions` rather than the
  `UrdfCollisions` component. The importer always uses that exact name.

### Dynamic topic subscription

`ROSConnection.GetTopicAndTypeList()` sends a `__topic_list` system command to
`ros_tcp_endpoint`; the reply is dispatched from `ROSConnection.Update()`, so callbacks land on
the main thread and can write straight to UI.

- `IRosTopicBinding` + `RosSubscriber<T>.SetTopic` — plain configuration before `Start`, an
  unsubscribe/resubscribe afterwards.
- `TopicBrowserUI` — lists topics filtered to the subscriber's message type, paged rather than
  scrolled (a `ScrollRect` is fussy to drag with a ray; fixed rows need no viewport mask or
  layout group).
- The browser runs its **own 5 s deadline**, because `GetTopicAndTypeList` has no failure path
  at all — a disconnected endpoint means the callback simply never fires, indistinguishable from
  a slow reply.

## Phase 6 — Sensor visualisation foundation (uncommitted)

Staged deliberately: foundation and LaserScan first, PointCloud2 rework deferred, so the
coordinate conversion could be verified against a simple 2D scan rather than debugged inside a
300k-point cloud.

- **`PlacementHandle`** — one generic grab handle replacing `RobotPlacementFollower` and
  `PanelPlacementFollower`, which were the same script twice. Their one real difference is
  preserved: an `ArticulationBody` root ignores writes to its Transform and must be moved with
  `TeleportRoot`.
- **`RosSubscriberLaserScan` + `LaserScanVisualizer`** — `sensor_msgs/LaserScan` rendered as
  range-coloured cubes.
- **`Shaders/VertexColorUnlit.shader`** — the built-in `Unlit/Color` ignores vertex colours, so
  the range gradient would have rendered flat. Unlit because passthrough MR has no useful scene
  lighting; stereo-aware because Quest uses single-pass instanced rendering.
- **Multi-target topic browser** — ◀ ▶ picks which subscriber to retarget; the list re-filters
  from the cached reply instead of costing another round trip.

Two conventions established here, now recorded in `CLAUDE.md`:

1. **Convert coordinates with `FLU.ConvertToRUF`** from the connector's own `ROSGeometry`
   namespace (`new Vector3(-v.y, v.z, v.x)`). ROS is right-handed Z-up, Unity left-handed Y-up.
   Don't hand-roll it.
2. **Draw into a Mesh under a MeshFilter, not `Graphics.DrawProcedural`.** DrawProcedural
   renders in world space and ignores the GameObject's Transform — so a `PlacementHandle` cannot
   move it. This is exactly why the existing PointCloud2 visualiser isn't handle-movable.

> Status: written but not compiled or run. The scene needs regenerating via
> **XRViz → Create MR MVP Scene (UR3e)** to pick it up.

## Phase 7 — Legibility pass (uncommitted)

Three handles that looked identical, and a panel that couldn't be read against a light wall.

### Handles: identical grey cubes → colour-coded spheres

Three 8 cm cubes in the default material were indistinguishable, and a cube's silhouette changes
with viewing angle, so at that size it reads as a *different object* depending on where you
stand. Now ~4 cm spheres — amber (robot), cyan (panel), magenta (laser scan) — with a matching
key on the status panel.

Amber/cyan/magenta rather than red/green/blue: the three stay distinguishable with the common
colour deficiencies, and none of them collide with the greys and skin tones that fill a
passthrough view.

Colour is defined once, in `k_HandleStyles`, and drives both the generated materials and the
panel key, so the two cannot drift apart. Two non-obvious constraints shaped the implementation:

- **The material has to be an asset.** A `new Material(...)` created by an editor script isn't
  saved anywhere the player build can find, so the handles would come out untinted in the APK.
  `Assets/XRViz/Materials/` is generated on first run and rewritten on every later one.
- **`VertexColorUnlit` could not be reused.** It multiplies by `Mesh.colors`, and a primitive
  sphere has no vertex colour channel at all — the tint would have depended on whatever the
  platform defaults the `COLOR` semantic to. Hence `Shaders/HandleUnlit.shader`, which also bakes
  in a fixed top-lit ramp and rim term, because a flat unlit sphere reads as a paper disc in
  stereo.

### Stale laser scan data persisted

A drawn scan is a mesh: it stays on screen after the data stops, and a frozen sweep is
indistinguishable from a live one. Two fixes, both routed through `LaserScanVisualizer.Clear()`:

- `_staleAfterSeconds` (3 s) blanks the mesh when nothing has arrived for that long. Timed from
  *arrival* (`Time.realtimeSinceStartup`), not the message header stamp — the header is the
  sensor's clock and need not agree with Unity's.
- A **Clear Scan** button on the panel.

`Clear()` also calls `RosSubscriberLaserScan.ClearData()`. Clearing only the mesh isn't enough:
the visualiser rebuilds it every frame from the subscriber's last parsed message, so the sweep
would reappear immediately.

### Anchors could be flung out of reach

Ray grab makes it trivial to throw a handle behind you or through a wall, with no way back short
of regenerating the scene. **Reset Anchors** returns every `PlacementHandle` to its start pose.

The default pose is captured at `Awake` rather than serialized, so a handle nudged in the Editor
before pressing Play resets to where you left it. `ResetToDefault` also has to write to the target
explicitly — `LateUpdate`'s moved-since-last-frame check would otherwise swallow the reset, since
the reset brings the handle and its last-known pose back into agreement in the same frame.

### Panels were unreadable over passthrough

The status panel had no background at all — light text directly over passthrough video of the
room. Now every panel goes through `StylePanel`: near-opaque dark background, header bar, hairline
dividers, buttons colour-coded by consequence with hover/press states. Built-in Unity UI sprites
(`Background.psd`, `UISprite.psd`, `Knob.psd`) for the rounded 9-slices, so no art was added.

`ControlPanelActions` also shows a transient confirmation line under the buttons. On a ray-driven
UI a near-miss produces *exactly nothing*, so "Clear Scan on an empty scan" and "the button didn't
register" looked the same; now they don't.

> Status: written but not compiled or run. Regenerate via
> **XRViz → Create MR MVP Scene (UR3e)**.

## Phase 8 — RGBD point cloud with a movable origin (uncommitted)

A point cloud from a simulated RGBD camera, whose **GameObject is the cloud's origin**: the
optical centre sits on the object and the cloud projects out along its +Z, so parking the green
handle where the real camera stands lands the virtual geometry on the real geometry.

Deliberately *not* built on `RosSubscriberPointCloud2`. A sim publishes depth and colour images;
`PointCloud2` would mean serialising a structured cloud on the ROS side and parsing it back on
the main thread in Unity, to arrive at exactly the same points the images already contain.

### What the existing pipeline could and couldn't give

`PointCloudRosGPU` already passes `transform.localToWorldMatrix` into its compute shader, so the
depth-image path was *already* origin-anchored — the "not handle-movable" note in `CLAUDE.md` was
true only of the `PointCloud2` variant, and has been narrowed. What it could not give:

- It takes `CompressedImage`, which needs `image_transport` republishers a sim doesn't run, and
  quantises depth to a 16-bit PNG when the sim's `32FC1` depth is already exact metric float.
- Its depth decode hardcodes 16-bit millimetres.
- It treats the camera frame as FLU.
- It requires the image to divide by 8, silently dropping the remainder.
- `PointCloudSquares.shader` has no stereo support and uses a geometry shader.

So: a new subscriber, compute shader, render shader and visualiser, with the old path left intact.

### Optical frame is not FLU

The convention most likely to waste an afternoon. `FLU.ConvertToRUF` is the standing rule in this
project, and it is the *wrong* transform for a camera. A `*_optical_frame` is right-down-forward
(REP 103/145, and the comment in `sensor_msgs/Image` itself), not front-left-up, so the conversion
to Unity is a plain Y flip — `(x, -y, z)`. The FLU shuffle lands the cloud on its side, which is
what the older compute shader does. `CLAUDE.md` now carries the exception.

### Encoding-driven upload

The old `RosSubscriberImage` was hardcoded to `TextureFormat.R8` and never read `encoding`, so it
could carry neither `rgb8` colour nor `32FC1` depth. Rewritten to pick the format from the
message, and to expose `GetDepthMetersPerUnit()` so the consumer doesn't care whether it was
handed `R16` millimetres (×65535×0.001, because R16 is UNorm) or `RFloat` metres (×1).

Two upload traps: `step` may carry row padding that `LoadRawTextureData` won't accept, so padded
frames are repacked into an exactly-sized reused buffer; and **no vertical flip is applied on
purpose** — ROS data starts top-left, Unity fills from bottom-left, and the two cancel so that
texel `(x, y)` is ROS pixel `(x, y)`, which is what keeps pixel coordinates agreeing with the
intrinsics.

### Rendering without a geometry shader

`PointCloudBillboard.shader` expands six vertices per point in the *vertex* shader from
`SV_VertexID`. Adreno has no native geometry stage — the driver emulates it, and on a 300k-point
cloud that emulation is the frame budget — and an ordinary vertex shader gets
`UNITY_VERTEX_OUTPUT_STEREO` for free, so the cloud reaches both eyes. Invalid points are marked
alpha 0 and pushed outside the clip volume, discarded before rasterisation rather than costing
fill.

### The limit is bandwidth, not the GPU

Raw 640×480 `rgb8` is 900 KB a frame; 640×480 `32FC1` depth is 1.2 MB. At 30 Hz that is ~60 MB/s
over a TCP socket to a headset on wifi, which will not work. This has to be throttled on the ROS
side — lower rate or smaller resolution. `_decimation` thins what is *drawn* (default 2, a quarter
of the points) and matters for frame rate, but the bytes have already crossed the network.

### Smaller things this forced

- `IClearableVisualization`, so **Clear Data** (was "Clear Scan") finds any visualisation holding
  geometry instead of naming types.
- `CreatePlacementHandle` gained a `rotation` argument. The cloud's handle is the only one with
  `_yawOnly` off — a camera has to be aimed — and a handle whose target keeps a non-identity
  rotation must *start* at that rotation, or the first grab snaps the target to identity.
- The topic browser's target label now shows the GameObject name: with two `sensor_msgs/Image`
  subscribers both reading "Image", picking the wrong one silently retargeted colour instead of
  depth.

> Status: written but not compiled or run. Regenerate via
> **XRViz → Create MR MVP Scene (UR3e)**, and assign the compute shader by hand if the generator
> warns it couldn't find it.

---

## Open items

| Item | Detail |
|---|---|
| **Android build blocked** | `Cannot include plugin 'assimp.dll' … already added`. Four URDF Importer plugins (`AssimpNet.dll`, `win/x86/assimp.dll`, `win/x86_64/assimp.dll`, `linux/x86_64/libassimp.so`) have "Any Platform" ticked with no Android exclusion, so the Android post-processor collects both `assimp.dll`s and they collide on filename. Fix requires embedding or forking the package — `Library/PackageCache` edits are wiped on reimport. **Decision pending.** |
| **PointCloud2 not headset-ready** | `RosSubscriberPointCloud2` parses on the main thread (307k points × 4 `BitConverter` calls + a `Color` alloc each), *requires* an `rgb` field and throws without one, and does no coordinate conversion. `PointCloudRosGPU_PointCloud2` draws at the world origin, ignoring its Transform. Unaddressed by Phase 8, which took the depth-image route instead — still needed for a lidar or a stereo cloud that only ever exists as `PointCloud2`. |
| **Legacy point cloud shader** | `PointCloudSquares.shader` has no `UNITY_VERTEX_OUTPUT_STEREO` support and uses a geometry shader, which Quest's Adreno GPU emulates slowly. Still used by `PointCloudRosGPU` and the `PointCloud2` variant; `PointCloudBillboard.shader` is the drop-in replacement if those get revived. |
| **RGBD bandwidth** | Raw colour + depth at 640×480/30 Hz is ~60 MB/s over the ROS TCP socket. Phase 8 documents this and decimates what it *draws*, but nothing throttles the stream — that has to happen ROS-side, or eventually via a `CompressedImage` path into the same visualiser. |
| **Phantom ray hits** | `ControlPanelMenuToggle` hides panels via `Canvas.enabled = false`, which does not disable the `GraphicRaycaster` or `RayInteractable` — a hidden panel can still swallow ray hits. More relevant now that three popups share the group. |
| **Deleted followers** | `RobotPlacementFollower` / `PanelPlacementFollower` were removed. Their only remaining consumers were the 23 scenes in `Assets/_Recovery/`, which is gitignored and untracked; those will show missing scripts. |
