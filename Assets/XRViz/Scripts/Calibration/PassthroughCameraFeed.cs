using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Unity.Robotics
{
    // One grabbed camera frame, together with everything needed to turn a marker found in it
    // into a pose in the room. The camera pose is captured at grab time and carried along,
    // because detection runs on a worker thread and the headset will have moved by the time it
    // finishes - using the camera's pose at that later moment is a silent few-centimetre error.
    public struct CameraFrame
    {
        public byte[] Gray;
        public int Width;
        public int Height;
        public PinholeIntrinsics Intrinsics;

        // Camera-to-world at the moment the pixels were taken
        public Vector3 CameraPosition;
        public Quaternion CameraRotation;

        // Brightness of the frame as a whole, gathered during the grayscale conversion because
        // the loop is already there. Only ever used to answer one question, and it is the first
        // question worth asking when detection finds nothing: are these pixels a picture of the
        // room at all? A camera that opens, reports a sane size and ticks didUpdateThisFrame but
        // delivers a uniform frame is indistinguishable from a dark room downstream - the
        // adaptive threshold marks nothing dark, so no contour is traced and no quad is found.
        public byte MinGray;
        public byte MaxGray;
        public byte MeanGray;

        // A frame with no spread at all cannot produce a detection whatever is in front of the
        // camera, so it is worth naming as a broken feed rather than as a failed search
        public bool IsUniform => MaxGray - MinGray < 8;

        public bool IsValid => Gray != null && Width > 0 && Height > 0 && Intrinsics.IsValid;
    }

    // Grayscale frames from the Quest's passthrough cameras, with intrinsics and a world pose.
    //
    // ANDROID ONLY. Passthrough camera access is gated behind horizonos.permission.HEADSET_CAMERA,
    // which exists only on the headset - there is no such camera over Quest Link and none in the
    // Editor. This component reports Unsupported there rather than pretending, so the calibration
    // button can say why instead of hanging for ten seconds. Testing this path means building the
    // APK; see Docs/ARUCO_CALIBRATION.md.
    //
    // Two numbers decide whether the calibration lands in the right place, and both come from
    // outside this project:
    //   * the INTRINSICS, read from the Android camera characteristics and rescaled to the
    //     resolution actually delivered - a wrong focal length scales the whole result, and a
    //     wrong principal point leans it;
    //   * the HEAD-TO-CAMERA offset, i.e. where the lens sits relative to tracking space. A
    //     constant error here is a constant error in the placed robot, and nothing downstream
    //     can detect it.
    // Both are read from the device when it will tell us and fall back to serialized values when
    // it will not, and which of the two happened is reported in Detail so it shows up in the UI
    // rather than as a quietly wrong robot.
    public class PassthroughCameraFeed : MonoBehaviour
    {
        public enum Eye
        {
            Left = 0,
            Right = 1,
        }

        // A vertical flip is a REFLECTION, not a rotation, so getting it wrong does not turn
        // the picture upside down in a way anyone would notice - it mirrors it. Quads are still
        // found (a square is a square either way) and every tag then decodes as nothing, because
        // the dictionary search covers the four rotations and no reflection. Hence an override:
        // the failure is diagnosable (see ArucoDictionary.IdentifiesWhenMirrored) but the value
        // WebCamTexture reports cannot be corrected from here if the device lies about it.
        public enum VerticalFlip
        {
            Auto,      // trust WebCamTexture.videoVerticallyMirrored
            Flip,
            DontFlip,
        }

        public enum FeedStatus
        {
            Idle,
            Unsupported,      // not an Android build - no passthrough camera exists here
            PermissionDenied,
            Opening,
            Running,
            Failed,
        }

        [Tooltip("Which passthrough camera to read. Either works; the left one is the " +
                 "conventional choice and matches the default head-to-camera offset below.")]
        [SerializeField] private Eye _eye = Eye.Left;

        [Tooltip("Camera device name to open, bypassing the automatic search. Leave empty " +
                 "unless the log says the search picked the wrong one - the device list is " +
                 "logged every time the camera opens.")]
        [SerializeField] private string _deviceNameOverride = "";

        [Tooltip("Resolution to ask the camera for. Higher reads a smaller tag from further " +
                 "away; the cost is per-frame readback, and detection is throttled anyway. " +
                 "Keep it square and equal to the sensor's active array (1280x1280 on a Quest " +
                 "3) so the factory intrinsics need no rescaling.")]
        // Square, and deliberately so. The Quest 3 passthrough sensor's active array is
        // 1280x1280 and its intrinsics (fx=fy=863.2, cx=643.7, cy=641.2) are quoted against
        // that array, so asking for 1280x1280 makes ResolveIntrinsics' rescale the identity and
        // the numbers apply verbatim.
        //
        // The obvious 1280x960 does not, and fails in a way that is invisible: ScaledTo assumes
        // a resize and would scale fy by 960/1280, but a 4:3 stream off a square sensor is a
        // vertical CROP, which leaves fy alone. That is a 25% error in one focal length only -
        // it does not blur or break anything, it just leans the solved pose, so the robot lands
        // confidently in the wrong place. Sidestep the question rather than guess the answer.
        [SerializeField] private Vector2Int _requestedResolution = new Vector2Int(1280, 1280);

        [SerializeField] private int _requestedFps = 30;

        [Tooltip("Whether to flip the camera rows. Auto trusts WebCamTexture." +
                 "videoVerticallyMirrored, which is right on every device that reports it " +
                 "honestly. If tags are seen but decode as MIRRORED, force the other setting.")]
        [SerializeField] private VerticalFlip _verticalFlip = VerticalFlip.Auto;

        [Tooltip("The tracked head transform - CenterEyeAnchor under the Camera Rig. The " +
                 "camera's pose is built out from this, so leaving it empty falls back to " +
                 "Camera.main and, failing that, refuses to produce frames.")]
        [SerializeField] private Transform _headAnchor;

        [Tooltip("Where the passthrough lens sits relative to the head anchor, in metres. Used " +
                 "when the device will not report its own lens pose. The default is roughly the " +
                 "Quest 3 left passthrough camera: up and to the left of the eye centre, and a " +
                 "little forward.")]
        [SerializeField] private Vector3 _headToCameraPosition = new Vector3(-0.045f, 0.017f, 0.012f);

        [Tooltip("Rotation of the camera relative to the head anchor, in degrees. The " +
                 "passthrough cameras look very slightly outward rather than straight ahead.")]
        [SerializeField] private Vector3 _headToCameraEuler = new Vector3(0f, 0f, 0f);

        [Tooltip("Intrinsics to use if the device will not report them. Left at zero, a pinhole " +
                 "model is synthesised from an assumed horizontal field of view instead.")]
        [SerializeField] private PinholeIntrinsics _fallbackIntrinsics;

        [Tooltip("Assumed horizontal field of view for the synthesised fallback intrinsics.")]
        [SerializeField] private float _fallbackHorizontalFovDegrees = 82f;

        private WebCamTexture _texture;
        private Color32[] _pixels;

        // Two grayscale buffers in rotation: the detector may still be reading the one handed
        // over last time while the next frame is being converted into the other
        private byte[][] _grayBuffers;
        private int _grayIndex;

        // Deadline for the camera list to appear after the permission is granted. Unity
        // enumerates the Android camera devices before the user has answered the permission
        // dialog, and the list it cached then is empty - it repopulates a few frames after the
        // grant, not immediately. Failing on the first empty list makes the very first run
        // after an install fail every time and every run afterwards work, which reads as a
        // flaky headset rather than as a race.
        private bool _loggedOrientation;
        private float _deviceWaitDeadline;
        private const float k_DeviceWaitSeconds = 3f;

        // How far off the head's forward axis the device's reported lens pose may be before it
        // is treated as a convention error rather than as a lens. Generous on purpose: the real
        // figure is about 11 degrees, and the failure this catches is 158.
        private const float k_MaxLensTiltDegrees = 45f;

        private PinholeIntrinsics _deviceIntrinsics;
        private bool _haveDeviceIntrinsics;
        private Vector3 _devicePosition;
        private Quaternion _deviceRotation = Quaternion.identity;
        private bool _haveDevicePose;

        public FeedStatus Status { get; private set; } = FeedStatus.Idle;

        // Human-readable explanation of Status, and of which intrinsics and extrinsics are in
        // use once running. Shown in the calibration panel.
        public string Detail { get; private set; } = "";

        public bool IsRunning => Status == FeedStatus.Running && _texture != null && _texture.isPlaying;

        public PinholeIntrinsics Intrinsics { get; private set; }

        // Why the last TryAcquireFrame produced nothing, or empty when it produced a frame.
        //
        // A camera that opens but never streams and a camera that streams into a blank room are
        // the same thing from outside: the search just ends with nothing. There are four quite
        // different reasons a frame does not arrive, and none of them can be told apart from
        // the headset without being named here.
        public string AcquireIssue { get; private set; } = "";

        // True when both intrinsics and lens pose came from the device rather than from the
        // serialized guesses. The calibration UI says so, because a guessed lens pose puts the
        // robot confidently in slightly the wrong place.
        public bool UsingDeviceCalibration => _haveDeviceIntrinsics && _haveDevicePose;

        public void Open()
        {
            if (IsRunning || Status == FeedStatus.Opening)
                return;

#if !UNITY_ANDROID || UNITY_EDITOR
            Status = FeedStatus.Unsupported;
            Detail = "Passthrough camera access is Android-only - it does not exist in the " +
                     "Editor or over Quest Link. Build and run the APK to calibrate.";
            return;
#else
            Status = FeedStatus.Opening;
            Detail = "requesting camera permission";

            if (!HasAllPermissions())
            {
                // Ask for both at once. Requesting them one dialog at a time works, but the
                // second request cannot be made until the first has been answered, which means
                // carrying a "which one are we waiting on" state for no benefit.
                Permission.RequestUserPermissions(k_RequiredPermissions);
                // The dialog is asynchronous; Update picks it up once the user has answered.
                // The enumeration clock does not start until then - it is measuring how long
                // the camera list takes to appear after the grant, not how long the user takes
                // to read the dialog.
                _deviceWaitDeadline = 0f;
                return;
            }

            _deviceWaitDeadline = Time.realtimeSinceStartup + k_DeviceWaitSeconds;
            OpenDevice();
#endif
        }

        public void Close()
        {
            _loggedOrientation = false;
            if (_texture != null)
            {
                if (_texture.isPlaying)
                    _texture.Stop();
                Destroy(_texture);
                _texture = null;
            }

            if (Status == FeedStatus.Running || Status == FeedStatus.Opening)
            {
                Status = FeedStatus.Idle;
                Detail = "";
            }
        }

        private void OnDisable()
        {
            Close();
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Resume opening once the permission dialog has been answered, then keep
            // retrying while the camera list is still catching up with the grant
            if (Status == FeedStatus.Opening && _texture == null && HasAllPermissions())
            {
                if (_deviceWaitDeadline <= 0f)
                    _deviceWaitDeadline = Time.realtimeSinceStartup + k_DeviceWaitSeconds;

                OpenDevice();
            }
#endif
        }

        // A frame is only produced when the camera has actually delivered new pixels; asking
        // every frame otherwise would re-detect the same image and waste a worker thread on it.
        public bool TryAcquireFrame(out CameraFrame frame)
        {
            frame = default;

            if (!IsRunning)
            {
                if (_texture == null)
                    AcquireIssue = "the camera texture was never created";
                else if (!_texture.isPlaying)
                    AcquireIssue = "WebCamTexture.Play() did not start the camera - Unity will " +
                                   "not stream a device it did not itself enumerate";
                else
                    AcquireIssue = $"the feed is {Status}";
                return false;
            }

            if (!_texture.didUpdateThisFrame)
            {
                AcquireIssue = "the camera is playing but has delivered no frames " +
                               $"({_texture.width}x{_texture.height})";
                return false;
            }

            int width = _texture.width;
            int height = _texture.height;
            if (width <= 16 || height <= 16)
            {
                // the texture reports 16x16 until the first frame lands
                AcquireIssue = "the camera texture is still 16x16";
                return false;
            }

            if (!TryGetCameraPose(out Vector3 position, out Quaternion rotation))
            {
                AcquireIssue = "no head pose is available - Head Anchor is unassigned and there " +
                               "is no main camera";
                return false;
            }

            AcquireIssue = "";

            EnsureBuffers(width, height);
            _texture.GetPixels32(_pixels);

            byte[] gray = _grayBuffers[_grayIndex];
            _grayIndex = 1 - _grayIndex;

            // GetPixels32 hands back the BOTTOM row first, the way graphics APIs store textures.
            // Every camera model - including the cx/cy in the intrinsics below - measures rows
            // from the TOP. Flipping here rather than anywhere later keeps one convention in the
            // detector and the solver; getting it wrong mirrors every pose, which looks almost
            // right and is completely wrong.
            bool flipVertically = _verticalFlip switch
            {
                VerticalFlip.Flip => true,
                VerticalFlip.DontFlip => false,
                _ => !_texture.videoVerticallyMirrored,
            };

            // Once, on the first frame that arrives. Everything about the image's orientation is
            // decided by these three values, none of them is visible from outside the headset,
            // and a wrong one costs a rebuild to find out about.
            if (!_loggedOrientation)
            {
                _loggedOrientation = true;
                Debug.Log($"[XRViz] Camera frame {width}x{height}: videoVerticallyMirrored=" +
                          $"{_texture.videoVerticallyMirrored}, videoRotationAngle=" +
                          $"{_texture.videoRotationAngle}, flip mode {_verticalFlip} -> " +
                          $"flipping rows: {flipVertically}. Intrinsics: {ResolveIntrinsics(width, height)}",
                          this);
            }

            byte minGray = 255, maxGray = 0;
            long graySum = 0;

            for (int y = 0; y < height; y++)
            {
                int sourceRow = (flipVertically ? height - 1 - y : y) * width;
                int destinationRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = _pixels[sourceRow + x];
                    // Rec. 601 luma in fixed point - the detector only ever compares against a
                    // local mean, so exact colourimetry does not matter, but consistency does
                    byte luma = (byte)((c.r * 77 + c.g * 150 + c.b * 29) >> 8);
                    gray[destinationRow + x] = luma;

                    if (luma < minGray) minGray = luma;
                    if (luma > maxGray) maxGray = luma;
                    graySum += luma;
                }
            }

            Intrinsics = ResolveIntrinsics(width, height);

            frame = new CameraFrame
            {
                Gray = gray,
                Width = width,
                Height = height,
                Intrinsics = Intrinsics,
                CameraPosition = position,
                CameraRotation = rotation,
                MinGray = minGray,
                MaxGray = maxGray,
                MeanGray = (byte)(graySum / (width * height)),
            };
            return frame.IsValid;
        }

        private void EnsureBuffers(int width, int height)
        {
            int count = width * height;
            if (_pixels == null || _pixels.Length != count)
                _pixels = new Color32[count];

            if (_grayBuffers == null || _grayBuffers[0] == null || _grayBuffers[0].Length != count)
                _grayBuffers = new[] { new byte[count], new byte[count] };
        }

        private PinholeIntrinsics ResolveIntrinsics(int width, int height)
        {
            if (_haveDeviceIntrinsics)
                return _deviceIntrinsics.ScaledTo(width, height);

            if (_fallbackIntrinsics.IsValid)
                return _fallbackIntrinsics.ScaledTo(width, height);

            // Last resort: a pinhole from an assumed field of view, square pixels, principal
            // point in the middle. Good enough to find a tag, not good enough to trust a
            // millimetre of the result - which is why Detail says so.
            float focal = 0.5f * width / Mathf.Tan(0.5f * _fallbackHorizontalFovDegrees * Mathf.Deg2Rad);
            return new PinholeIntrinsics
            {
                Fx = focal,
                Fy = focal,
                Cx = 0.5f * width,
                Cy = 0.5f * height,
                Width = width,
                Height = height,
            };
        }

        // Camera-to-world, built out from the tracked head. The lens pose relative to the head
        // comes from the device when it reports one and from the serialized offset otherwise.
        private bool TryGetCameraPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            Transform head = _headAnchor;
            if (head == null && Camera.main != null)
                head = Camera.main.transform;

            if (head == null)
                return false;

            Vector3 localPosition = _haveDevicePose ? _devicePosition : _headToCameraPosition;
            Quaternion localRotation = _haveDevicePose
                ? _deviceRotation
                : Quaternion.Euler(_headToCameraEuler);

            position = head.TransformPoint(localPosition);
            rotation = head.rotation * localRotation;
            return true;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string k_HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";

        // Both are needed. Requesting them together rather than only the Horizon one means
        // this does not quietly depend on something else in the app having asked for CAMERA
        // first - the Meta XR packages do today, which is exactly the kind of thing that is
        // true until a package is removed.
        // Meta's vendor metadata identifying the passthrough cameras. camera_source 0 is a
        // passthrough camera; position is 0 for the left eye and 1 for the right, matching Eye.
        private const string k_CameraSourceKey = "com.meta.extra_metadata.camera_source";
        private const string k_CameraPositionKey = "com.meta.extra_metadata.position";
        private const int k_CameraSourcePassthrough = 0;

        private static readonly string[] k_RequiredPermissions =
        {
            "android.permission.CAMERA",
            k_HeadsetCameraPermission,
        };

        private static bool HasAllPermissions()
        {
            foreach (string permission in k_RequiredPermissions)
            {
                if (!Permission.HasUserAuthorizedPermission(permission))
                    return false;
            }
            return true;
        }

        private static string MissingPermissions()
        {
            var missing = new List<string>();
            foreach (string permission in k_RequiredPermissions)
            {
                if (!Permission.HasUserAuthorizedPermission(permission))
                    missing.Add(permission);
            }
            return string.Join(", ", missing);
        }

        private void OpenDevice()
        {
            if (!HasAllPermissions())
            {
                Status = FeedStatus.PermissionDenied;
                Detail = $"Camera permission was refused ({MissingPermissions()}). Grant it in " +
                         "the headset's app permission settings, then try again.";
                return;
            }

            var devices = WebCamTexture.devices;
            string[] cameraIds = GetCameraIds();
            string passthroughId = FindPassthroughCameraId(_eye);

            // The camera2 id and the name WebCamTexture wants are two different strings - see
            // ResolveDevice. The id is kept because the factory calibration is read with it.
            string cameraId = passthroughId;
            string deviceName;
            string how;

            if (!string.IsNullOrEmpty(_deviceNameOverride))
            {
                // Checked before the enumeration rather than inside it: the case an override is
                // most needed for is precisely the one where Unity lists no devices to match it
                // against, so requiring it to appear in that list would disable it exactly when
                // it matters.
                deviceName = _deviceNameOverride;
                how = "override";
            }
            else if (devices != null && devices.Length > 0)
            {
                if (!ResolveDevice(devices, cameraIds, passthroughId, out deviceName, out how))
                {
                    Status = FeedStatus.Failed;
                    Detail = $"The {_eye} passthrough camera is camera2 id '{passthroughId}', but " +
                             "it could not be matched to any of the cameras Unity enumerated " +
                             $"([{string.Join(", ", Names(devices))}] against camera2's " +
                             $"{DescribeCameraIds()}). Set Device Name Override on the feed to " +
                             "one of Unity's names to force the choice.";
                    Debug.LogWarning($"[XRViz] {Detail}", this);
                    return;
                }
            }
            else
            {
                // Unity has nothing. Wait it out first: the post-grant race empties Unity's list
                // for a few frames after the permission is answered.
                //
                // There is deliberately no "open the camera2 id anyway" fallback here. That
                // reads as the robust choice and is the opposite: WebCamTexture only streams a
                // device it enumerated itself, so it answers a camera2 id with "Cannot find
                // webcam device 50", never plays, and leaves the search timing out against a
                // camera that was never opened.
                if (Time.realtimeSinceStartup < _deviceWaitDeadline)
                {
                    Detail = "waiting for the camera list";
                    return;
                }

                Status = FeedStatus.Failed;
                Detail = $"Unity enumerated no camera devices after {k_DeviceWaitSeconds:F0} s " +
                         $"(camera2 reports {DescribeCameraIds()}). If the camera permission was " +
                         "only just granted, close and reopen the app - the device list is built " +
                         "at startup. Otherwise passthrough camera access needs Horizon OS v74 " +
                         "or newer on a Quest 3 or 3S.";
                Debug.LogWarning($"[XRViz] {Detail}", this);
                return;
            }

            // Both lists, every time. Which cameras exist and which one was opened is the single
            // thing that cannot be worked out from outside the headset, and picking the wrong
            // camera is silent: it opens, plays, reports isPlaying, and delivers solid black.
            Debug.Log($"[XRViz] Unity cameras: [{string.Join(", ", Names(devices))}]; camera2: " +
                      $"{DescribeCameraIds()}; passthrough {_eye}: " +
                      $"'{passthroughId ?? "not identified"}' -> opening '{deviceName}' ({how})",
                      this);

            _texture = new WebCamTexture(deviceName, _requestedResolution.x, _requestedResolution.y,
                _requestedFps);
            _texture.Play();

            ReadDeviceCalibration(cameraId);

            Status = FeedStatus.Running;
            Detail = DescribeCalibrationSource(deviceName);
        }

        private static List<string> Names(WebCamDevice[] devices)
        {
            var names = new List<string>();
            if (devices != null)
            {
                foreach (var device in devices)
                    names.Add(device.name);
            }
            return names;
        }

        // Which of the headset's cameras is the passthrough one for this eye, as a name
        // WebCamTexture will actually accept.
        //
        // Two separate traps here, and both look like a dark room downstream.
        //
        // A Quest 3 reports FIVE cameras, not two, and the passthrough pair is not at the front
        // of the list - so indexing that list by eye opens an unrelated camera, which plays
        // happily and returns black. Ask the device which cameras are the passthrough ones
        // rather than assuming an order; that is what FindPassthroughCameraId does.
        //
        // But what it answers with is a camera2 id, and that is NOT the name Unity knows the
        // device by. On Horizon OS v207 camera2 calls them "1", "50", "51" while Unity
        // enumerates the same three as "Camera 0", "Camera 1", "Camera 2". Passing the id
        // straight to the WebCamTexture constructor logs "Cannot find webcam device 50" and
        // hands back a texture that never plays - so no frame ever reaches the detector, and
        // the search just times out as though the tag were missing.
        //
        // What does hold between the two lists is the ORDER: both are the same camera service
        // enumeration, so the nth id is the nth device. That only makes sense while the lists
        // are the same length, so it is checked rather than assumed - if Unity ever hides a
        // camera that camera2 reports, the indices shift and this would silently open the
        // wrong lens.
        private bool ResolveDevice(WebCamDevice[] devices, string[] cameraIds,
            string passthroughId, out string deviceName, out string how)
        {
            if (!string.IsNullOrEmpty(passthroughId))
            {
                // Kept first because it is the unambiguous case, and because a future Unity or
                // Horizon OS naming devices after the camera2 id would land here and need no
                // further thought.
                foreach (var device in devices)
                {
                    if (device.name == passthroughId)
                    {
                        deviceName = device.name;
                        how = $"passthrough camera for the {_eye} eye, matched by name";
                        return true;
                    }
                }

                if (cameraIds != null && cameraIds.Length == devices.Length)
                {
                    int index = Array.IndexOf(cameraIds, passthroughId);
                    if (index >= 0)
                    {
                        deviceName = devices[index].name;
                        how = $"passthrough camera for the {_eye} eye, camera2 id " +
                              $"'{passthroughId}' is device #{index}";
                        return true;
                    }
                }

                deviceName = null;
                how = null;
                return false;
            }

            // Last resort, and the old behaviour: index the list by eye. Kept so an unfamiliar
            // device list still opens something rather than failing outright, but it is a guess
            // and the log says so.
            deviceName = devices[Mathf.Clamp((int)_eye, 0, devices.Length - 1)].name;
            how = "GUESSED by list order - no passthrough camera was identified";
            return true;
        }

        // Meta tags each camera with vendor metadata saying whether it is a passthrough camera
        // and which eye it belongs to. These keys have no static field to reference, so they are
        // looked up by name in the characteristics' own key list - which also means an OS that
        // does not publish them returns null here rather than throwing.
        private string DescribeCameraIds()
        {
            string[] ids = GetCameraIds();
            if (ids == null)
                return "unavailable";
            return ids.Length == 0 ? "none" : $"[{string.Join(", ", ids)}]";
        }

        private static string[] GetCameraIds()
        {
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera");
                return manager.Call<string[]>("getCameraIdList");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string FindPassthroughCameraId(Eye eye)
        {
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera");

                string[] ids = manager.Call<string[]>("getCameraIdList");
                if (ids == null)
                    return null;

                foreach (string id in ids)
                {
                    using var characteristics =
                        manager.Call<AndroidJavaObject>("getCameraCharacteristics", id);

                    if (!TryGetVendorByte(characteristics, k_CameraSourceKey, out int source)
                        || source != k_CameraSourcePassthrough)
                        continue;

                    if (!TryGetVendorByte(characteristics, k_CameraPositionKey, out int position))
                        continue;

                    if (position == (int)eye)
                        return id;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[XRViz] Could not identify the passthrough cameras from the " +
                                 $"camera metadata: {exception.Message}", this);
            }

            return null;
        }

        private static bool TryGetVendorByte(AndroidJavaObject characteristics, string keyName,
            out int value)
        {
            value = 0;

            using var keys = characteristics.Call<AndroidJavaObject>("getKeys");
            if (keys == null)
                return false;

            int count = keys.Call<int>("size");
            for (int i = 0; i < count; i++)
            {
                using var key = keys.Call<AndroidJavaObject>("get", i);
                if (key == null || key.Call<string>("getName") != keyName)
                    continue;

                // Meta publishes these as a one-element byte array. sbyte, not byte: a Java byte
                // is signed, and asking Unity for the unsigned one works but logs two
                // deprecation warnings per key per camera - fifteen stack traces in the middle
                // of the one log that has to be readable when the camera will not open.
                var bytes = characteristics.Call<sbyte[]>("get", key);
                if (bytes == null || bytes.Length == 0)
                    return false;

                value = bytes[0];
                return true;
            }

            return false;
        }

        private string DescribeCalibrationSource(string deviceName)
        {
            string intrinsics = _haveDeviceIntrinsics
                ? "intrinsics from device"
                : (_fallbackIntrinsics.IsValid
                    ? "SERIALIZED intrinsics"
                    : $"ASSUMED intrinsics ({_fallbackHorizontalFovDegrees:F0} deg HFOV)");

            string pose = _haveDevicePose ? "lens pose from device" : "SERIALIZED lens offset";
            return $"{deviceName} - {intrinsics}, {pose}";
        }

        // Android's camera2 characteristics carry the factory calibration for each lens. These
        // are standard AOSP keys, not Meta vendor ones, which is why they are worth trying: if
        // the device fills them in, the calibration is the manufacturer's own and beats anything
        // serialized here. Every step is guarded - an unsupported key returns null rather than
        // throwing, but a missing class or a security failure does throw, and a calibration
        // button must not take the app down with it.
        // Takes the CAMERA2 ID, not the WebCamTexture device name - the two are different
        // strings on Horizon OS (see ResolveDevice), and the characteristics are keyed by the
        // id. Passing the device name here reads no calibration at all and silently falls back
        // to the serialized guess.
        private void ReadDeviceCalibration(string cameraId)
        {
            _haveDeviceIntrinsics = false;
            _haveDevicePose = false;

            if (string.IsNullOrEmpty(cameraId))
            {
                // Only reachable when the passthrough camera was never identified and the device
                // was guessed by list order. Reading a guessed camera's factory calibration is
                // worse than having none: the serialized fallback is at least known to be a
                // guess, while an unrelated lens's intrinsics look authoritative.
                Debug.LogWarning("[XRViz] No passthrough camera2 id was identified, so no " +
                    "factory calibration could be read; using the serialized offset and " +
                    "intrinsics instead.", this);
                return;
            }

            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera");

                string[] ids = manager.Call<string[]>("getCameraIdList");
                if (ids == null || ids.Length == 0)
                    return;

                if (Array.IndexOf(ids, cameraId) < 0)
                {
                    Debug.LogWarning($"[XRViz] Camera id '{cameraId}' is not in the camera2 id " +
                        "list, so no factory calibration could be read for it.", this);
                    return;
                }

                string id = cameraId;

                using var characteristics = manager.Call<AndroidJavaObject>("getCameraCharacteristics", id);
                using var keys = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics");

                float[] calibration = GetFloatArray(characteristics, keys, "LENS_INTRINSIC_CALIBRATION");
                if (calibration != null && calibration.Length >= 4)
                {
                    // [fx, fy, cx, cy, skew], expressed against the active sensor array
                    if (TryGetActiveArraySize(characteristics, keys, out int arrayWidth, out int arrayHeight))
                    {
                        _deviceIntrinsics = new PinholeIntrinsics
                        {
                            Fx = calibration[0],
                            Fy = calibration[1],
                            Cx = calibration[2],
                            Cy = calibration[3],
                            Width = arrayWidth,
                            Height = arrayHeight,
                        };
                        _haveDeviceIntrinsics = _deviceIntrinsics.IsValid;
                    }
                }

                float[] translation = GetFloatArray(characteristics, keys, "LENS_POSE_TRANSLATION");
                float[] quaternion = GetFloatArray(characteristics, keys, "LENS_POSE_ROTATION");
                if (translation != null && translation.Length >= 3
                    && quaternion != null && quaternion.Length >= 4)
                {
                    // POSITION. Android's device frame is right-handed with y up and z out of
                    // the display - which on a headset points backwards, at the wearer. Unity's
                    // head frame is left-handed with z forward. Negating z is the whole
                    // conversion, and it flips the handedness at the same time.
                    _devicePosition = new Vector3(translation[0], translation[1], -translation[2]);

                    // ROTATION, and it is NOT the same swap applied to a quaternion.
                    //
                    // LENS_POSE_ROTATION is not "how the lens is tilted relative to the head".
                    // It is the rotation from the device frame INTO the camera's own frame,
                    // where z runs along the optical axis and y points DOWN the image. For any
                    // outward-facing camera that contains a ~180 degrees flip about x, because
                    // the camera looks along -z of a device frame whose +z faces the wearer.
                    // On a Quest 3 it reads as 169 degrees about -x: the 180 flip, less the
                    // 11 degrees the passthrough camera is tilted down by.
                    //
                    // Treating it as a head-to-camera tilt therefore aims the camera BACKWARDS
                    // and upside down - 158 degrees away from where it is actually looking.
                    // Detection is unaffected, because that happens in image space; only the
                    // placement moves, so the tag is found and the robot lands metres away.
                    //
                    // Undo it properly. Inverting gives the camera's axes in the device frame;
                    // Unity wants +y up rather than the image's +y down, so that axis flips too;
                    // and the whole basis then maps into the head frame with the same z negation
                    // as the position. Composed, that is a plain permutation of the components:
                    //
                    //     (x, y, z, w)_android  ->  (w, -z, -y, -x)_unity
                    //
                    // Worth a sanity check if this is ever touched: the result must leave the
                    // camera looking very nearly straight ahead, a few degrees DOWN. Anything
                    // near 180 degrees means this conversion has been "simplified" back into the
                    // handedness swap above, which is the one thing it is not.
                    _deviceRotation = new Quaternion(quaternion[3], -quaternion[2],
                        -quaternion[1], -quaternion[0]);

                    // And check it, because the failure mode above is silent. A passthrough
                    // camera bolted to the front of a headset looks where the wearer looks, to
                    // within the few degrees it is tilted by. Anything further out is a
                    // convention error, not a lens, and a convention error here does not degrade
                    // the calibration - it relocates the robot. The serialized offset is a
                    // guess, but it is a guess that points forwards.
                    float offAxis = Vector3.Angle(_deviceRotation * Vector3.forward, Vector3.forward);
                    if (offAxis > k_MaxLensTiltDegrees)
                    {
                        Debug.LogError($"[XRViz] The device's lens pose puts the {_eye} passthrough " +
                            $"camera {offAxis:F0} deg off the head's forward axis, which cannot be " +
                            "right for a passthrough camera. Ignoring it and using the serialized " +
                            "head-to-camera offset instead; the calibration will be off by a " +
                            "constant, rather than badly wrong. Check the LENS_POSE_ROTATION " +
                            "conversion in ReadDeviceCalibration.", this);
                    }
                    else
                    {
                        _haveDevicePose = true;
                        Debug.Log($"[XRViz] Lens pose from device: offset {_devicePosition}, " +
                                  $"looking {offAxis:F1} deg off head-forward.", this);
                    }
                }
            }
            catch (Exception e)
            {
                // Falls through to the serialized values, which is a working calibration with a
                // constant offset rather than no calibration at all
                Debug.LogWarning($"[XRViz] Could not read passthrough camera calibration from the " +
                    $"device ({e.GetType().Name}: {e.Message}); using the serialized offset and " +
                    "intrinsics instead.", this);
            }
        }

        private static float[] GetFloatArray(AndroidJavaObject characteristics,
            AndroidJavaClass keys, string keyName)
        {
            try
            {
                using var key = keys.GetStatic<AndroidJavaObject>(keyName);
                return key == null ? null : characteristics.Call<float[]>("get", key);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryGetActiveArraySize(AndroidJavaObject characteristics,
            AndroidJavaClass keys, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using var key = keys.GetStatic<AndroidJavaObject>("SENSOR_INFO_ACTIVE_ARRAY_SIZE");
                using var rect = characteristics.Call<AndroidJavaObject>("get", key);
                if (rect == null)
                    return false;

                width = rect.Call<int>("width");
                height = rect.Call<int>("height");
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
#endif
    }
}
