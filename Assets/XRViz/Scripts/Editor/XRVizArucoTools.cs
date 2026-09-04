using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Robotics;

// Editor-side companions to the ArUco chassis calibration.
//
// The exporter exists because the printed tag is the ground truth for the whole feature: the
// detector, the dictionary tables and the pose solve are all verifiable, but if the tag on the
// robot came from a different dictionary, or was scaled by a print dialogue's "fit to page", the
// calibration is wrong in a way nothing in the app can detect. Generating the tag from the same
// tables the detector reads removes one of those two risks; printing it at a stated physical size
// and then MEASURING it removes the other.
//
// The self-check exists because this feature cannot be exercised without a headset - passthrough
// camera access is Android-only - so the maths needs a way to be run and trusted from the desk.
public static class XRVizArucoTools
{
    // Physical layout of the printed sheet. The quiet zone is not decoration: the detector finds
    // the marker by tracing the outside of its black border, which it cannot do if the border
    // runs into something else dark.
    private const int k_QuietZoneCells = 1;

    [MenuItem("XRViz/Export ArUco Calibration Tag...")]
    public static void ExportTag()
    {
        var window = ScriptableObject.CreateInstance<ArucoTagExportWindow>();
        window.titleContent = new GUIContent("Export ArUco Tag");
        window.minSize = new Vector2(380f, 260f);
        window.ShowUtility();
    }

    // Draws the marker at an exact whole number of pixels per cell, so every cell is crisp and
    // no resampling can shave a row off the border
    public static Texture2D RenderTag(ArucoDictionaryName dictionaryName, int id, int pixelsPerCell)
    {
        var dictionary = ArucoDictionary.Get(dictionaryName);
        ulong code = dictionary.GetCode(id);

        int grid = dictionary.GridSize;
        int cells = grid + 2;                              // the marker's own black border
        int totalCells = cells + 2 * k_QuietZoneCells;     // plus the white margin around it
        int size = totalCells * pixelsPerCell;

        var texture = new Texture2D(size, size, TextureFormat.RGB24, false)
        {
            filterMode = FilterMode.Point,
        };

        var pixels = new Color32[size * size];
        var white = new Color32(255, 255, 255, 255);
        var black = new Color32(0, 0, 0, 255);

        for (int y = 0; y < size; y++)
        {
            // Texture rows run bottom-up; marker rows run top-down
            int cellRow = (size - 1 - y) / pixelsPerCell - k_QuietZoneCells;

            for (int x = 0; x < size; x++)
            {
                int cellColumn = x / pixelsPerCell - k_QuietZoneCells;

                bool isWhite;
                if (cellRow < 0 || cellColumn < 0 || cellRow >= cells || cellColumn >= cells)
                    isWhite = true;                                        // quiet zone
                else if (cellRow == 0 || cellColumn == 0 || cellRow == cells - 1 || cellColumn == cells - 1)
                    isWhite = false;                                       // marker border
                else
                    isWhite = dictionary.IsBitSet(code, cellRow - 1, cellColumn - 1);

                pixels[y * size + x] = isWhite ? white : black;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    // ------------------------------------------------------------------------------------
    // Self-check
    // ------------------------------------------------------------------------------------

    [MenuItem("XRViz/Check ArUco Detector")]
    public static void RunSelfCheck()
    {
        var settings = new ArucoDetectorSettings
        {
            Dictionary = ArucoDictionaryName.Dict4x4_50,
            MinSideLengthPixels = 20f,
        };
        var dictionary = ArucoDictionary.Get(settings.Dictionary);
        var detector = new ArucoDetector(settings);

        var intrinsics = new PinholeIntrinsics
        {
            Fx = 720f, Fy = 720f, Cx = 640f, Cy = 480f, Width = 1280, Height = 960,
        };
        const float markerSize = 0.15f;

        int cases = 0, detected = 0, correctId = 0;
        float worstPosition = 0f, worstAngle = 0f, worstReprojection = 0f;

        foreach (int id in new[] { 0, 7, 23, 49 })
        foreach (float spin in new[] { 0f, 90f, 180f, -90f, 35f })
        foreach (float tilt in new[] { 0f, 20f, 40f })
        foreach (float depth in new[] { 0.4f, 0.8f, 1.5f })
        {
            cases++;

            Matrix4x4 rotation = Matrix4x4.Rotate(
                Quaternion.AngleAxis(180f + tilt, Vector3.right) * Quaternion.AngleAxis(spin, Vector3.forward));
            var translation = new Vector3(0.05f * depth, -0.03f * depth, depth);

            byte[] gray = RenderSynthetic(dictionary, id, markerSize, intrinsics, rotation, translation);
            var found = detector.Detect(gray, intrinsics.Width, intrinsics.Height);

            if (found.Count != 1)
                continue;

            detected++;
            if (found[0].Id != id)
                continue;
            correctId++;

            var pose = MarkerPoseSolver.Solve(found[0].Corners, markerSize, intrinsics);
            if (!pose.Valid)
                continue;

            // Compare against physical directions, not a rebuilt quaternion, so a convention
            // mistake in the solver cannot cancel itself out here
            var expectedPosition = new Vector3(translation.x, -translation.y, translation.z);
            Vector3 expectedForward = FlipY(rotation.GetColumn(2)).normalized;
            Vector3 expectedUp = FlipY(rotation.GetColumn(1)).normalized;

            worstPosition = Mathf.Max(worstPosition, (pose.Position - expectedPosition).magnitude);
            worstAngle = Mathf.Max(worstAngle,
                Mathf.Max(Vector3.Angle(pose.Rotation * Vector3.forward, expectedForward),
                          Vector3.Angle(pose.Rotation * Vector3.up, expectedUp)));
            worstReprojection = Mathf.Max(worstReprojection, pose.ReprojectionErrorPixels);
        }

        // The dictionary tables are data lifted from OpenCV, so the thing worth asserting about
        // them is internal consistency: every id must identify as itself at every rotation, and
        // report the rotation that gets back to how it was printed
        int rotationChecks = 0, rotationFailures = 0;
        foreach (ArucoDictionaryName name in Enum.GetValues(typeof(ArucoDictionaryName)))
        {
            var d = ArucoDictionary.Get(name);
            for (int id = 0; id < d.MarkerCount; id++)
            {
                ulong turned = d.GetCode(id);
                for (int turns = 0; turns < 4; turns++)
                {
                    rotationChecks++;
                    bool ok = d.TryIdentify(turned, out int gotId, out int gotRotation, out int errors);
                    if (!ok || gotId != id || errors != 0 || gotRotation != ((4 - turns) & 3))
                        rotationFailures++;
                    turned = ArucoDictionary.RotateClockwise(turned, d.GridSize);
                }
            }
        }

        // The mirror diagnostic, which is what tells a flipped camera feed apart from a tag
        // printed from the wrong dictionary. Two things have to hold: every mirrored view must
        // be recognised as mirrored, and no unmirrored code may be mistaken for one.
        //
        // The ids listed at the end are the ones where mirroring is UNDETECTABLE, because the
        // code is symmetric under reflection and so decodes to itself either way. Those give a
        // silently reflected pose on a flipped feed rather than a failure, which is the one
        // outcome nothing downstream can catch - so a printed tag should not use them.
        int mirrorChecks = 0, mirrorMissed = 0;
        var ambiguous = new List<string>();
        foreach (ArucoDictionaryName name in Enum.GetValues(typeof(ArucoDictionaryName)))
        {
            var d = ArucoDictionary.Get(name);
            var symmetric = new List<int>();
            for (int id = 0; id < d.MarkerCount; id++)
            {
                ulong turned = d.GetCode(id);
                bool selfMirrored = false;
                for (int turns = 0; turns < 4; turns++)
                {
                    ulong mirroredView = ArucoDictionary.MirrorHorizontal(turned, d.GridSize);
                    mirrorChecks++;
                    if (!d.IdentifiesWhenMirrored(mirroredView))
                        mirrorMissed++;
                    if (d.TryIdentify(mirroredView, out _, out _, out _))
                        selfMirrored = true;
                    turned = ArucoDictionary.RotateClockwise(turned, d.GridSize);
                }
                if (selfMirrored)
                    symmetric.Add(id);
            }
            if (symmetric.Count > 0)
                ambiguous.Add($"{name}: {string.Join(", ", symmetric)}");
        }

        bool passed = detected == cases && correctId == cases && rotationFailures == 0
            && mirrorMissed == 0
            && worstPosition < 0.006f && worstAngle < 3f;

        string report =
            $"[XRViz] ArUco self-check: {(passed ? "PASS" : "FAIL")}\n" +
            $"  dictionaries: {rotationChecks} id/rotation lookups, {rotationFailures} wrong\n" +
            $"  mirror check: {mirrorChecks} mirrored views, {mirrorMissed} not recognised\n" +
            $"  synthetic views: {detected}/{cases} detected, {correctId}/{cases} identified\n" +
            $"  worst position error   {worstPosition * 1000f:F2} mm\n" +
            $"  worst orientation error {worstAngle:F2} deg\n" +
            $"  worst reprojection      {worstReprojection:F3} px\n" +
            "  mirror-AMBIGUOUS ids (a flipped feed places these silently reflected\n" +
            "  instead of failing, so do not print one):\n    " +
            (ambiguous.Count == 0 ? "none" : string.Join("\n    ", ambiguous));

        if (passed)
            Debug.Log(report);
        else
            Debug.LogError(report);
    }

    private static Vector3 FlipY(Vector3 v) => new Vector3(v.x, -v.y, v.z);

    // Back-project every pixel onto the marker plane: exact by construction, so any error the
    // detector shows is the detector's
    private static byte[] RenderSynthetic(ArucoDictionary dictionary, int id, float sizeMetres,
        PinholeIntrinsics k, Matrix4x4 rotation, Vector3 translation)
    {
        var gray = new byte[k.Width * k.Height];
        float half = 0.5f * sizeMetres;
        int cells = dictionary.GridSize + 2;
        ulong code = dictionary.GetCode(id);

        Vector3 axisX = rotation.GetColumn(0);
        Vector3 axisY = rotation.GetColumn(1);
        Vector3 normal = rotation.GetColumn(2);
        float normalDotTranslation = Vector3.Dot(normal, translation);

        for (int v = 0; v < k.Height; v++)
        {
            for (int u = 0; u < k.Width; u++)
            {
                gray[v * k.Width + u] = 235;

                var ray = new Vector3((u + 0.5f - k.Cx) / k.Fx, (v + 0.5f - k.Cy) / k.Fy, 1f);
                float denominator = Vector3.Dot(normal, ray);
                if (Mathf.Abs(denominator) < 1e-9f)
                    continue;

                float distance = normalDotTranslation / denominator;
                if (distance <= 0f)
                    continue;

                Vector3 onPlane = ray * distance - translation;
                float x = Vector3.Dot(onPlane, axisX);
                float y = Vector3.Dot(onPlane, axisY);
                if (Mathf.Abs(x) > half || Mathf.Abs(y) > half)
                    continue;

                int column = Mathf.Clamp((int)((x + half) / (2f * half) * cells), 0, cells - 1);
                int row = Mathf.Clamp((int)((half - y) / (2f * half) * cells), 0, cells - 1);

                bool white = row > 0 && column > 0 && row < cells - 1 && column < cells - 1
                             && dictionary.IsBitSet(code, row - 1, column - 1);

                gray[v * k.Width + u] = white ? (byte)240 : (byte)25;
            }
        }

        return gray;
    }
}

// Small utility window for the export, so the dictionary, id and printed size are chosen
// deliberately rather than baked in - they have to match what ArucoRobotCalibrator is
// configured with, and a mismatch is silent.
public class ArucoTagExportWindow : EditorWindow
{
    private ArucoDictionaryName _dictionary = ArucoDictionaryName.Dict4x4_50;
    private int _id = 0;
    private float _markerSizeMillimetres = 150f;
    private int _dpi = 300;

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Printable chassis tag", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "These must match the ArucoRobotCalibrator's Dictionary, Marker Id and Marker Size. " +
            "Marker size is the side of the BLACK SQUARE, not of the printed page - measure the " +
            "print before trusting it.", MessageType.Info);

        _dictionary = (ArucoDictionaryName)EditorGUILayout.EnumPopup("Dictionary", _dictionary);

        var dictionary = ArucoDictionary.Get(_dictionary);
        _id = EditorGUILayout.IntSlider("Marker id", Mathf.Clamp(_id, 0, dictionary.MarkerCount - 1),
            0, dictionary.MarkerCount - 1);

        _markerSizeMillimetres = EditorGUILayout.FloatField("Marker size (mm)", _markerSizeMillimetres);
        _dpi = Mathf.Max(72, EditorGUILayout.IntField("Print resolution (DPI)", _dpi));

        int cells = dictionary.GridSize + 2;
        float cellMillimetres = _markerSizeMillimetres / cells;
        int pixelsPerCell = Mathf.Max(1, Mathf.RoundToInt(cellMillimetres / 25.4f * _dpi));
        float actualMarkerMillimetres = pixelsPerCell * cells * 25.4f / _dpi;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Each cell", $"{cellMillimetres:F2} mm ({pixelsPerCell} px)");
        // Cells are drawn at a whole number of pixels, so the printed marker lands on the
        // nearest size that allows that - say so rather than let it be a surprise at the ruler
        EditorGUILayout.LabelField("Printed marker", $"{actualMarkerMillimetres:F2} mm at {_dpi} DPI");

        if (Mathf.Abs(actualMarkerMillimetres - _markerSizeMillimetres) > 0.5f)
        {
            EditorGUILayout.HelpBox(
                $"Rounding to whole pixels per cell shifts the marker to " +
                $"{actualMarkerMillimetres:F2} mm. Set the calibrator's Marker Size to that, or " +
                "raise the DPI.", MessageType.Warning);
        }

        EditorGUILayout.Space();
        if (!GUILayout.Button("Export PNG..."))
            return;

        string path = EditorUtility.SaveFilePanel("Export ArUco calibration tag",
            "", $"aruco_{_dictionary}_{_id}_{actualMarkerMillimetres:F0}mm.png", "png");
        if (string.IsNullOrEmpty(path))
            return;

        var texture = XRVizArucoTools.RenderTag(_dictionary, _id, pixelsPerCell);
        try
        {
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Debug.Log($"[XRViz] Wrote {_dictionary} id {_id} to {path}. Print it at 100% scale " +
                      $"(no 'fit to page'), then measure the black square: it should be " +
                      $"{actualMarkerMillimetres:F1} mm across. Set ArucoRobotCalibrator's " +
                      "Marker Size to whatever you measure, in metres.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }

        Close();
    }
}
