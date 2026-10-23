# README media

Captures referenced by the root [README.md](../../README.md). Each `<img>` there points at a
file in this folder by exact name — GitHub paths are case-sensitive, so rename the file or the
reference together, never one alone.

| File | Shot | README section |
|---|---|---|
| `DynamicIpConnection.gif` | Typing the endpoint IP on the panel keypad and pressing Connect. | Connect from the headset |
| `MultiTopicTypes.gif` | Binding several visualisers to different topics from the Topics/Views pages. | Many topics, many views |
| `StaleData.gif` | A topic stops publishing and its visualisation clears itself. | Stale data never looks live |
| `ARuCoDetection.gif` | Looking at the printed chassis tag → gizmo → TF world snapping into place. | Calibrate against the real robot |
| `PickAndPlace.gif` | The virtual arm tracking the real robot through a pick-and-place. | Mirror the real robot |
| `LaserScan.gif` | A LaserScan lying on the real walls and furniture. | Sensors in the room (left) |
| `Pointcloud.gif` | The RGBD point cloud plus its camera image window. | Sensors in the room (right) |
| `rgbdcameranopointcloud.png` | The real room through passthrough, RealSense in the foreground. | Sensors in the room, comparison (left) |
| `rgbdcamera.png` | The same view with the RGBd Camera cloud drawn over it. | Sensors in the room, comparison (right) |
| `ur3e+rg2.png` | UR3e + RG2 in XRViz. | Supported robots |
| `ur5e+rg6.png` | UR5e + RG6 in XRViz. | Supported robots |
| `PR2.png` | PR2 in XRViz. | Supported robots |
| `unitree-g1.png` | Unitree G1 in XRViz. | Supported robots |

Capture from the headset (APK recording, or Quest Link screen capture) in the room with the real
robot, so passthrough is visible — a shot against a black void does not show what XRViz is for.

Guidelines: GIFs under ~5 MB (trim and drop to 15 fps rather than shortening below ~5 s),
landscape, and crop out the empty passthrough margins so the subject fills the frame.
