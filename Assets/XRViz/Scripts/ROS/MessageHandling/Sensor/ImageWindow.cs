using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // A ROS camera image on a floating window in the room: a quad carrying the live texture, a
    // backing panel behind it, and a caption saying what is on screen and whether it is live.
    //
    // Deliberately NOT placed from TF. A camera image is a picture, not geometry - there is no
    // pose at which it is "correct", so it goes where you want to look at it, and it is moved by
    // its grab handle like the control panel. The handle is yaw-only, so the window stays upright
    // however it is dragged: a picture tipped out of vertical is unreadable, and unlike a point
    // cloud there is no reason ever to tilt one.
    //
    // Replaces the older ImageToMeshRenderer, which drew the texture straight onto a default
    // material. That gets you an upside-down picture (ROS rows run top-down, Unity texels run
    // bottom-up), red-and-blue swapped on the bgr8 encodings that are just as common as rgb8, a
    // pure-red picture for mono8, and a lit material that renders near-black under passthrough.
    // All four are handled by XRViz/ImageUnlit, driven from the encoding the message itself
    // reports, so nothing here has to be configured per camera.
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class ImageWindow : MonoBehaviour, IClearableVisualization
    {
        [SerializeField] private RosSubscriberImage _imageSub;

        [Tooltip("Height of the window in metres. Width follows the image's own aspect ratio, so " +
                 "a 4:3 and a 16:9 camera both fill this height and differ in width.")]
        [SerializeField] private float _heightMetres = 0.45f;

        [Tooltip("Blank the window when nothing has arrived for this long. A frozen frame looks " +
                 "exactly like a live one. 0 keeps the last frame indefinitely.")]
        [SerializeField] private float _staleAfterSeconds = 3f;

        [Tooltip("Brightness multiplier. Raw 16-bit depth samples at about 0.03 at two metres - " +
                 "i.e. black - so a depth topic needs this well above 1 to show anything.")]
        [SerializeField] private float _gain = 1f;

        [Tooltip("Caption under the window: topic, resolution, encoding and state.")]
        [SerializeField] private bool _showCaption = true;

        [SerializeField] private Material _material;

        private const string k_ShaderName = "XRViz/ImageUnlit";

        // Panel palette, matching the control panel: near-opaque dark, because anything
        // translucent over passthrough video of a light wall is unreadable
        private static readonly Color k_Backing = new Color(0.06f, 0.07f, 0.09f, 0.95f);
        private static readonly Color k_TextPrimary = new Color(0.93f, 0.95f, 0.97f);
        private static readonly Color k_TextMuted = new Color(0.62f, 0.67f, 0.73f);

        private const string k_ColorOk = "#4CAF50";
        private const string k_ColorWarn = "#FFB300";

        private MeshRenderer _renderer;
        private Material _materialInstance;
        private Transform _backing;
        private TextMeshProUGUI _caption;

        private int _appliedWidth = -1;
        private int _appliedHeight = -1;
        private string _appliedEncoding;
        private bool _blank = true;
        private float _nextCaptionRefresh;

        private void Awake()
        {
            _renderer = GetComponent<MeshRenderer>();

            // The quad primitive's mesh, so this works on a bare GameObject. Its own +Z faces the
            // viewer, and the shader draws both sides, so the window reads from behind too.
            var filter = GetComponent<MeshFilter>();
            if (filter.sharedMesh == null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                filter.sharedMesh = quad.GetComponent<MeshFilter>().sharedMesh;
                Destroy(quad);
            }

            if (_material != null)
            {
                _materialInstance = new Material(_material);
            }
            else
            {
                var shader = Shader.Find(k_ShaderName);
                if (shader == null)
                {
                    Debug.LogError($"[XRViz] {name}: shader '{k_ShaderName}' not found and no " +
                        "material assigned, so the image has nothing to draw with. It must be in " +
                        "Project Settings > Graphics > Always Included Shaders to survive a " +
                        "player build. Disabling.", this);
                    enabled = false;
                    return;
                }
                _materialInstance = new Material(shader);
            }

            _renderer.material = _materialInstance;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            BuildBacking();
            if (_showCaption)
                BuildCaption();

            SetBlank(true);
        }

        // Wipes the window and drops the subscriber's decoded texture with it - blanking only the
        // window would let the next frame put the same stale picture back. Found automatically by
        // the control panel's Clear Data button.
        public void Clear()
        {
            if (_imageSub != null)
                _imageSub.ClearData();
            SetBlank(true);
        }

        private void Update()
        {
            if (_imageSub == null)
                return;

            if (Time.unscaledTime >= _nextCaptionRefresh)
            {
                _nextCaptionRefresh = Time.unscaledTime + 0.5f;
                RenderCaption();
            }

            float arrival = _imageSub.GetLastMessageRealtime();
            if (_staleAfterSeconds > 0f &&
                (arrival < 0f || Time.realtimeSinceStartup - arrival > _staleAfterSeconds))
            {
                SetBlank(true);
                return;
            }

            if (!_imageSub.isReady())
                return;

            var texture = _imageSub.GetLatestTexture2D();
            if (texture == null)
                return;

            int width = _imageSub.GetImageWidth();
            int height = _imageSub.GetImageHeight();
            string encoding = _imageSub.GetEncoding();

            // Only on a change: the texture object is reused between frames, and so is the shape
            if (width != _appliedWidth || height != _appliedHeight || encoding != _appliedEncoding)
            {
                _appliedWidth = width;
                _appliedHeight = height;
                _appliedEncoding = encoding;
                ApplyShape(texture, width, height, encoding);
            }

            _materialInstance.mainTexture = texture;
            SetBlank(false);
        }

        // Aspect from the image, corrections from its encoding. Both come out of the message, so
        // pointing this at a different camera needs nothing configured.
        private void ApplyShape(Texture texture, int width, int height, string encoding)
        {
            if (width > 0 && height > 0)
            {
                float aspect = width / (float)height;
                transform.localScale = new Vector3(_heightMetres * aspect, _heightMetres, 1f);
                if (_backing != null)
                {
                    // A hair larger, and a hair behind, so it reads as a frame rather than
                    // z-fighting with the picture
                    _backing.localScale = new Vector3(1.04f, 1.06f, 1f);
                    _backing.localPosition = new Vector3(0f, 0f, 0.002f);
                }
            }

            bool mono = texture is Texture2D t2d &&
                (t2d.format == TextureFormat.R8 || t2d.format == TextureFormat.R16 ||
                 t2d.format == TextureFormat.RFloat);

            _materialInstance.SetFloat("_Mono", mono ? 1f : 0f);
            _materialInstance.SetFloat("_Bgr", !mono && _imageSub.IsBgr() ? 1f : 0f);
            _materialInstance.SetFloat("_FlipY", 1f);
            _materialInstance.SetFloat("_Gain", Mathf.Max(1f, _gain));
        }

        private void SetBlank(bool blank)
        {
            if (_blank == blank)
                return;
            _blank = blank;

            // The backing stays: a window that vanishes entirely reads as "I lost the panel"
            // rather than "the camera stopped"
            _renderer.enabled = !blank;
            if (blank)
                _materialInstance.mainTexture = null;
        }

        private void BuildBacking()
        {
            var backing = GameObject.CreatePrimitive(PrimitiveType.Quad);
            backing.name = "Backing";
            Destroy(backing.GetComponent<Collider>());
            backing.transform.SetParent(transform, false);
            backing.transform.localPosition = new Vector3(0f, 0f, 0.002f);
            backing.transform.localScale = new Vector3(1.04f, 1.06f, 1f);
            _backing = backing.transform;

            var renderer = backing.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Shader shader = Shader.Find("XRViz/HandleUnlit") ?? Shader.Find("Unlit/Color");
            if (shader == null)
                return;

            var material = new Material(shader) { name = "Image Window Backing" };
            material.SetColor("_Color", k_Backing);
            material.SetColor("_RimColor", k_Backing);
            renderer.material = material;
        }

        private void BuildCaption()
        {
            var canvasGo = new GameObject("Caption", typeof(Canvas));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            // Authored in UI units and scaled down, like the panels. Parented under a quad whose
            // localScale carries the image aspect, so the lossy scale is undone here - otherwise
            // the caption stretches with the picture.
            var rect = canvasGo.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(600f, 60f);
            canvasGo.transform.localRotation = Quaternion.identity;

            var background = new GameObject("Background", typeof(Image));
            background.transform.SetParent(canvasGo.transform, false);
            var backgroundImage = background.GetComponent<Image>();
            backgroundImage.color = k_Backing;
            backgroundImage.raycastTarget = false;
            var backgroundRect = backgroundImage.rectTransform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            var textGo = new GameObject("Text", typeof(TextMeshProUGUI));
            textGo.transform.SetParent(canvasGo.transform, false);
            _caption = textGo.GetComponent<TextMeshProUGUI>();
            _caption.fontSize = 22f;
            _caption.alignment = TextAlignmentOptions.Center;
            _caption.color = k_TextPrimary;
            _caption.raycastTarget = false;
            var textRect = _caption.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 0f);
            textRect.offsetMax = new Vector2(-10f, 0f);
        }

        private void LateUpdate()
        {
            if (_caption == null)
                return;

            // This object's scale carries the image aspect and changes with the camera, so the
            // caption's own scale is recomputed from it rather than set once - otherwise a 16:9
            // image stretches the text and a tall one squashes it.
            var lossy = transform.lossyScale;
            float sx = Mathf.Approximately(lossy.x, 0f) ? 1f : 1f / lossy.x;
            float sy = Mathf.Approximately(lossy.y, 0f) ? 1f : 1f / lossy.y;
            _caption.transform.parent.localScale = new Vector3(0.0006f * sx, 0.0006f * sy, 1f);
            _caption.transform.parent.localPosition = new Vector3(0f, -0.56f, 0f);
        }

        private void RenderCaption()
        {
            if (_caption == null || _imageSub == null)
                return;

            string topic = string.IsNullOrEmpty(_imageSub.Topic) ? "<no topic>" : _imageSub.Topic;

            if (string.IsNullOrEmpty(_imageSub.Topic))
            {
                _caption.text = $"<color=#{ColorUtility.ToHtmlStringRGB(k_TextMuted)}>" +
                    "no topic - pick one on the panel's Topics tab</color>";
                return;
            }

            float arrival = _imageSub.GetLastMessageRealtime();
            string state;
            if (arrival < 0f)
                state = $"<color={k_ColorWarn}>waiting</color>";
            else
            {
                float age = Time.realtimeSinceStartup - arrival;
                state = age > 1f
                    ? $"<color={k_ColorWarn}>{age:0.0} s ago</color>"
                    : $"<color={k_ColorOk}>live</color>";
            }

            string size = _appliedWidth > 0 ? $"{_appliedWidth}x{_appliedHeight}" : "";
            string encoding = string.IsNullOrEmpty(_appliedEncoding) ? "" : $" {_appliedEncoding}";

            _caption.text = $"{topic}  <size=80%><color=#" +
                ColorUtility.ToHtmlStringRGB(k_TextMuted) + $">{size}{encoding}</color></size>  {state}";
        }

        private void OnDestroy()
        {
            if (_materialInstance != null)
                Destroy(_materialInstance);
        }
    }
}
