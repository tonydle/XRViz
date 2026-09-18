using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Robotics
{
    // Says, in the room, what the TF origin is.
    //
    // RosTfTree's Transform is the fixed frame: every TF-anchored visualisation in the scene is
    // measured out from this one object, and the ArUco calibration moves THIS rather than the
    // robot whenever the robot's pose comes from TF (see ArucoRobotCalibrator.ApplyToTfOrigin).
    // That makes it the single most consequential object in the scene and, without something
    // drawn here, the least visible one: an empty GameObject with three thin bars on it and a
    // grab sphere floating a metre above.
    //
    // So this draws a label at the origin carrying the three things you cannot get from the bars
    // alone - that this is the fixed frame, whether /tf is actually arriving into it, and how
    // many visualisations are currently being placed from it (i.e. how much moves when it moves).
    // It also flashes when the origin is re-placed, because a calibration that works and a
    // calibration that silently did nothing look identical the moment after the button press.
    //
    // Everything it needs is built at runtime, so adding this component to the TF Origin object
    // is the whole installation - no prefab, no wiring, and it works in a scene the generator
    // hasn't been re-run over.
    //
    // Runs after the default order so the billboard uses the pose PlacementHandle wrote in its
    // own LateUpdate, rather than trailing it by a frame.
    [DefaultExecutionOrder(100)]
    public class TfOriginIndicator : MonoBehaviour
    {
        [Tooltip("Draw the floating label from the start. Off by default: what the fixed frame " +
                 "is doing is a question you ask occasionally, not one worth a card permanently " +
                 "parked over the robot. The axis bars are unaffected either way.")]
        [SerializeField] private bool _showLabel = false;

        [Tooltip("Controller button that shows and hides the card. B on the right controller by " +
                 "default - Y on the left already hides the camera image window, and Start (the " +
                 "Menu button) is the control panel's. None disables the binding.")]
        [SerializeField] private OVRInput.Button _toggleButton = OVRInput.Button.Two;

        [SerializeField] private OVRInput.Controller _toggleController = OVRInput.Controller.RTouch;

        [Tooltip("How far above the origin the label floats, in metres. Measured along world up, " +
                 "not the origin's own up, so the label stays overhead after a calibration " +
                 "rotates the fixed frame.")]
        [SerializeField] private float _labelHeightMetres = 0.32f;

        [Tooltip("How far the label stands off horizontally towards whoever is looking at it, in " +
                 "metres. The fixed frame is usually the robot's base, which puts the label " +
                 "inside the chassis mesh at any readable height - this leans it out in front " +
                 "instead. Zero keeps it directly over the origin.")]
        [SerializeField] private float _labelStandoffMetres = 0.28f;

        [Tooltip("Build the red/green/blue axis bars if this object has none. Off for a scene " +
                 "whose triad is authored by the scene generator - which is the usual case, and " +
                 "is detected anyway.")]
        [SerializeField] private bool _buildAxesIfMissing = true;

        [SerializeField] private float _axisLengthMetres = 0.15f;
        [SerializeField] private float _axisThicknessMetres = 0.008f;

        [Tooltip("Seconds without a /tf message before the label calls the tree quiet. This is " +
                 "arrival time, not the message header stamp - a frozen publisher and a clock " +
                 "skew are different problems.")]
        [SerializeField] private float _quietAfterSeconds = 2f;

        [Tooltip("How long the label keeps reporting a move after the origin is re-placed.")]
        [SerializeField] private float _moveHighlightSeconds = 8f;

        [Tooltip("Seconds between label refreshes. The state it shows changes on its own as " +
                 "topics arrive, so it re-renders on a timer rather than only on a move.")]
        [SerializeField] private float _refreshSeconds = 0.5f;

        // Same palette as the world-space panels (XRVizCreateMVPScene): dark and near-opaque,
        // because a translucent label over passthrough video of a light wall is unreadable.
        // Duplicated rather than shared - the palette lives in an Editor-only script.
        private static readonly Color k_Background = new Color(0.06f, 0.07f, 0.09f, 0.95f);
        private static readonly Color k_Header = new Color(0.11f, 0.33f, 0.52f, 1f);
        private static readonly Color k_HeaderMoved = new Color(0.16f, 0.50f, 0.30f, 1f);
        private static readonly Color k_TextPrimary = new Color(0.93f, 0.95f, 0.97f);
        private static readonly Color k_TextMuted = new Color(0.62f, 0.67f, 0.73f);

        // Status colours match the panels' (TfAnchorPanelUI) so green/amber/red mean the same
        // thing wherever they appear
        private const string k_ColorOk = "#4CAF50";
        private const string k_ColorWarn = "#FFB300";
        private const string k_ColorBad = "#FF5252";

        // Authored in UI units like the panels, then scaled to metres. 440 x 200 at 0.0008
        // is a 0.35 m card - big enough to read from across the room, small enough not to
        // occlude the robot it sits on.
        private const float k_LabelWidthUnits = 440f;
        private const float k_LabelHeightUnits = 200f;
        private const float k_LabelUnitsToMetres = 0.0008f;
        private const float k_HeaderHeightUnits = 46f;

        private const string k_AxisNamePrefix = "TF Axis";

        private RosTfTree _tree;
        private Transform _label;
        private Image _headerImage;
        private TextMeshProUGUI _bodyText;
        private Transform _head;

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private float _movedRealtime = -1f;
        private string _movedReason;
        private float _nextRefresh;

        // A move reported by the thing doing the moving wins over one merely observed, for this
        // long - both fire for the same calibration, and "moved by ArUco calibration" is the
        // more useful of the two answers.
        private const float k_ReasonHoldSeconds = 0.5f;

        private void Awake()
        {
            _tree = GetComponent<RosTfTree>();
            if (_tree == null)
            {
                // Still useful on a child of the origin, but not on some unrelated object: the
                // label would then claim a fixed frame that is somewhere else entirely.
                _tree = RosTfTree.Instance;
                if (_tree != null && _tree.transform != transform)
                    Debug.LogWarning($"[XRViz] {nameof(TfOriginIndicator)} on '{name}' is not on the " +
                        $"{nameof(RosTfTree)} ('{_tree.name}'), so it is labelling a pose that is not " +
                        "the fixed frame.", this);
            }

            _lastPosition = transform.position;
            _lastRotation = transform.rotation;

            if (_buildAxesIfMissing)
                EnsureAxes();

            BuildLabel();
            SetLabelVisible(_showLabel);
        }

        // Polled here on the indicator itself, which VisibilityHotkey cannot do for the things
        // it toggles: hiding this card deactivates only the label child, so this component keeps
        // running and can still hear the button that brings the card back.
        private void Update()
        {
            if (_toggleButton != OVRInput.Button.None &&
                OVRInput.GetDown(_toggleButton, _toggleController))
                ToggleLabel();
        }

        private void LateUpdate()
        {
            DetectMove();
            PlaceLabel();

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + Mathf.Max(0.1f, _refreshSeconds);
                Render();
            }
        }

        // ---------------------------------------------------------------------------------
        // Move reporting
        // ---------------------------------------------------------------------------------

        // Called by whatever deliberately re-places the fixed frame, so the label can name it.
        // A move is noticed either way (DetectMove), but "moved by hand" and "moved by ArUco
        // calibration" are worth telling apart when the question is whether the calibration did
        // anything at all.
        public void ShowMoved(string reason)
        {
            _movedRealtime = Time.realtimeSinceStartup;
            _movedReason = reason;
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            Render();
        }

        // Convenience for callers that hold the origin's Transform rather than this component -
        // which is everyone, since the indicator is optional. No-ops when there is none.
        public static void NotifyMoved(Transform origin, string reason)
        {
            if (origin == null)
                return;

            var indicator = origin.GetComponentInChildren<TfOriginIndicator>(true);
            if (indicator != null)
                indicator.ShowMoved(reason);
        }

        private void DetectMove()
        {
            bool moved = (transform.position - _lastPosition).sqrMagnitude > 1e-8f
                || Quaternion.Angle(transform.rotation, _lastRotation) > 0.05f;
            if (!moved)
                return;

            _lastPosition = transform.position;
            _lastRotation = transform.rotation;

            // The calibration moves the origin through its PlacementHandle, whose write lands a
            // frame or so after the ShowMoved call - don't let that overwrite the real reason.
            if (Time.realtimeSinceStartup - _movedRealtime > k_ReasonHoldSeconds)
            {
                _movedRealtime = Time.realtimeSinceStartup;
                _movedReason = "moved by hand";
            }

            Render();
        }

        // ---------------------------------------------------------------------------------
        // Label
        // ---------------------------------------------------------------------------------

        public bool LabelVisible => _label != null && _label.gameObject.activeSelf;

        public void SetLabelVisible(bool visible)
        {
            _showLabel = visible;
            if (_label != null)
                _label.gameObject.SetActive(visible);
        }

        public void ToggleLabel()
        {
            SetLabelVisible(!LabelVisible);
        }

        private void PlaceLabel()
        {
            if (_label == null || !_label.gameObject.activeSelf)
                return;

            // World up, not the origin's own up: a calibration can leave the fixed frame tilted,
            // and a label lying on its side is worse than no label.
            Vector3 position = transform.position + Vector3.up * _labelHeightMetres;

            var head = ResolveHead();
            if (head == null)
            {
                _label.position = position;
                return;
            }

            // Lean out towards the viewer, horizontally so the card keeps its height. The fixed
            // frame normally sits at the robot's base, i.e. inside the chassis mesh, and a label
            // buried in the trolley is a label nobody reads.
            Vector3 toHead = head.position - position;
            toHead.y = 0f;
            if (_labelStandoffMetres > 0f && toHead.sqrMagnitude > 1e-4f)
                position += toHead.normalized * _labelStandoffMetres;

            _label.position = position;

            // A world-space Canvas reads from the side its local -Z faces, so the canvas's
            // forward points the same way the viewer is looking. Vector3.up as the up vector
            // keeps the text level rather than rolling with the head.
            Vector3 away = position - head.position;
            if (away.sqrMagnitude > 1e-6f)
                _label.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        private Transform ResolveHead()
        {
            if (_head != null)
                return _head;

            // Camera.main rather than a serialized reference: the camera comes from a Meta
            // Building Block rig that this script has no business reaching into, and it is the
            // same fallback PassthroughCameraFeed uses.
            if (Camera.main != null)
                _head = Camera.main.transform;
            return _head;
        }

        private void Render()
        {
            if (_bodyText == null)
                return;

            bool moved = _movedRealtime > 0f
                && Time.realtimeSinceStartup - _movedRealtime < _moveHighlightSeconds;

            if (_headerImage != null)
                _headerImage.color = moved ? k_HeaderMoved : k_Header;

            var text = new System.Text.StringBuilder();
            text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(k_TextMuted))
                .Append(">ground truth for all sensor data</color>\n");
            text.Append(DescribeTree()).Append('\n');
            text.Append(DescribeAnchors());

            if (moved)
            {
                float age = Time.realtimeSinceStartup - _movedRealtime;
                text.Append("\n<color=").Append(k_ColorOk).Append(">re-placed: ")
                    .Append(_movedReason).Append("  <size=85%>(").Append(age.ToString("0"))
                    .Append(" s ago)</size></color>");
            }

            _bodyText.text = text.ToString();
        }

        // What the tree itself is doing. The distinction that matters is "nothing has arrived"
        // versus "something arrived and then stopped" - the first is a connection or a topic
        // name, the second is a node that died with the visualisations still showing its last
        // frame as if it were live.
        private string DescribeTree()
        {
            if (_tree == null)
                return $"<color={k_ColorBad}>no RosTfTree - nothing is placed from here</color>";

            string fixedFrame = string.IsNullOrEmpty(_tree.FixedFrame)
                ? "each chain's root"
                : _tree.FixedFrame;

            if (!_tree.HasFrames)
                return $"<color={k_ColorWarn}>waiting for /tf</color>  <size=85%>({fixedFrame})</size>";

            float age = Time.realtimeSinceStartup - _tree.LastMessageRealtime;
            string state = _quietAfterSeconds > 0f && age > _quietAfterSeconds
                ? $"<color={k_ColorWarn}>/tf quiet {age:0.0} s</color>"
                : $"<color={k_ColorOk}>/tf live</color>";

            return $"{_tree.FrameCount} frames · {state}  <size=85%>({fixedFrame})</size>";
        }

        // How much moves when this moves. Zero anchored is the case worth seeing: the origin can
        // be dragged around the room all day and nothing will follow it.
        private string DescribeAnchors()
        {
            var anchors = FindObjectsByType<TfAnchor>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (anchors.Length == 0)
                return $"<color=#{ColorUtility.ToHtmlStringRGB(k_TextMuted)}>no TF anchors in the scene</color>";

            int anchored = 0;
            foreach (var anchor in anchors)
            {
                if (anchor.Status == TfAnchor.AnchorStatus.Anchored)
                    anchored++;
            }

            string color = anchored > 0
                ? "#" + ColorUtility.ToHtmlStringRGB(k_TextMuted)
                : k_ColorWarn;
            return $"<color={color}>{anchored} of {anchors.Length} placed from here</color>";
        }

        private void BuildLabel()
        {
            var labelGo = new GameObject("TF Origin Label", typeof(Canvas));
            labelGo.transform.SetParent(transform, false);
            _label = labelGo.transform;

            var canvas = labelGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = labelGo.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(k_LabelWidthUnits, k_LabelHeightUnits);
            labelGo.transform.localScale = Vector3.one * k_LabelUnitsToMetres;

            // No GraphicRaycaster and nothing raycastable on it: this is a sign, not a control,
            // and it floats right where the ray goes looking for the handles behind it.
            var background = CreateStretchedImage(labelGo.transform, "Background", k_Background);
            background.rectTransform.offsetMin = Vector2.zero;
            background.rectTransform.offsetMax = Vector2.zero;

            _headerImage = CreateStretchedImage(labelGo.transform, "Header", k_Header);
            _headerImage.rectTransform.anchorMin = new Vector2(0f, 1f);
            _headerImage.rectTransform.anchorMax = new Vector2(1f, 1f);
            _headerImage.rectTransform.pivot = new Vector2(0.5f, 1f);
            _headerImage.rectTransform.sizeDelta = new Vector2(0f, k_HeaderHeightUnits);
            _headerImage.rectTransform.anchoredPosition = Vector2.zero;

            var headerText = CreateText(_headerImage.transform, "Header Text", 26f,
                TextAlignmentOptions.Center);
            headerText.text = "TF ORIGIN — fixed frame";
            headerText.color = k_TextPrimary;
            headerText.fontStyle = FontStyles.Bold;

            _bodyText = CreateText(labelGo.transform, "Body Text", 19f, TextAlignmentOptions.Top);
            _bodyText.rectTransform.offsetMin = new Vector2(14f, 12f);
            _bodyText.rectTransform.offsetMax = new Vector2(-14f, -(k_HeaderHeightUnits + 10f));
            _bodyText.color = k_TextPrimary;

            Render();
        }

        private static Image CreateStretchedImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            var rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return image;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, float size,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.alignment = alignment;
            text.richText = true;
            text.raycastTarget = false;

            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return text;
        }

        // ---------------------------------------------------------------------------------
        // Axes
        // ---------------------------------------------------------------------------------

        // The same triad the scene generator draws (XRVizCreateMVPScene.CreateAxisTriad), rebuilt
        // here only when there isn't one - so this component is self-sufficient on a bare
        // GameObject without doubling up the generator's bars in the scene it authored.
        //
        // Coloured by ROS convention (x red, y green, z blue) and drawn along the ROS axes, not
        // Unity's: ROS +x forward is Unity +z, ROS +y left is Unity -x, ROS +z up is Unity +y.
        private void EnsureAxes()
        {
            foreach (Transform child in transform)
            {
                if (child.name.StartsWith(k_AxisNamePrefix))
                    return;
            }

            (string Name, Vector3 UnityDirection, Color Color)[] axes =
            {
                ("X (ROS forward)", Vector3.forward, new Color(0.95f, 0.26f, 0.21f)),
                ("Y (ROS left)", Vector3.left, new Color(0.30f, 0.85f, 0.39f)),
                ("Z (ROS up)", Vector3.up, new Color(0.26f, 0.52f, 0.96f)),
            };

            // Lit shaders render near-black under passthrough - the constraint every visualisation
            // in this project shares. HandleUnlit is the solid-colour one.
            Shader shader = Shader.Find("XRViz/HandleUnlit") ?? Shader.Find("Unlit/Color");

            foreach (var axis in axes)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bar.name = $"{k_AxisNamePrefix} {axis.Name}";
                bar.transform.SetParent(transform, false);
                // The cube's local +z becomes the bar's length, so this aims it. FromToRotation
                // rather than LookRotation: up is degenerate for LookRotation's default up vector.
                bar.transform.localRotation =
                    Quaternion.FromToRotation(Vector3.forward, axis.UnityDirection);
                bar.transform.localPosition = axis.UnityDirection * (_axisLengthMetres * 0.5f);
                bar.transform.localScale = new Vector3(_axisThicknessMetres, _axisThicknessMetres,
                    _axisLengthMetres);

                // Marker, not a control: a collider here sits in front of the origin and eats ray
                // hits meant for the handle behind it
                Destroy(bar.GetComponent<Collider>());

                if (shader == null)
                    continue;

                var material = new Material(shader) { name = $"TFAxis{axis.Name[0]} (runtime)" };
                material.SetColor("_Color", axis.Color);
                // A near-white rim, matching the handles: keeps the silhouette crisp against a
                // busy passthrough background without washing the hue out
                material.SetColor("_RimColor", Color.Lerp(axis.Color, Color.white, 0.75f));
                bar.GetComponent<MeshRenderer>().material = material;
            }
        }
    }
}
