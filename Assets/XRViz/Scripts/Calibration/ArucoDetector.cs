using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Robotics
{
    // One decoded marker: its id and its four corners in image pixels.
    //
    // Corners are in the marker's OWN order - Corners[0] is the printed top-left, then top-right,
    // bottom-right, bottom-left, which is clockwise in image space (x right, y DOWN). That order
    // is what makes the pose solve unambiguous, so it is resolved here from the dictionary
    // rotation rather than left to the caller.
    public struct ArucoDetection
    {
        public int Id;
        public Vector2[] Corners;

        // How many bits had to be corrected to reach this id. 0 is an exact read; anything up to
        // the dictionary's MaxCorrectionBits is still a valid identification, but a run of
        // non-zero values means the tag is too small, too far, or too blurred to trust.
        public int BitErrors;

        // Mean intensity gap between the marker's white cells and its black cells, 0..1. A tag
        // read in poor light or badly out of focus still decodes, but with a small gap - which is
        // the honest signal that the corner positions underneath it are mush.
        public float Contrast;

        public Vector2 Center
        {
            get
            {
                if (Corners == null || Corners.Length != 4)
                    return Vector2.zero;
                return 0.25f * (Corners[0] + Corners[1] + Corners[2] + Corners[3]);
            }
        }

        // Length of the shortest side in pixels. The useful "is this big enough to trust?"
        // number: pose accuracy from a planar marker degrades with the square of apparent size.
        public float MinSideLength
        {
            get
            {
                if (Corners == null || Corners.Length != 4)
                    return 0f;
                float min = float.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    float side = (Corners[(i + 1) & 3] - Corners[i]).magnitude;
                    if (side < min)
                        min = side;
                }
                return min;
            }
        }
    }

    [Serializable]
    public class ArucoDetectorSettings
    {
        [Tooltip("Which printed dictionary the calibration tag belongs to.")]
        public ArucoDictionaryName Dictionary = ArucoDictionaryName.Dict4x4_50;

        [Tooltip("Half-width of the adaptive threshold window in pixels. Roughly the largest " +
                 "lighting gradient the binarisation should ignore; too small eats the marker's " +
                 "own cells.")]
        [Range(3, 60)] public int ThresholdWindowRadius = 12;

        [Tooltip("How far below the local mean a pixel must be to count as black. Raises " +
                 "robustness to sensor noise on flat surfaces at the cost of thin dark lines.")]
        [Range(0, 40)] public int ThresholdOffset = 7;

        [Tooltip("Reject quads whose shortest side is under this many pixels - too few pixels " +
                 "per cell to read reliably.")]
        public float MinSideLengthPixels = 24f;

        [Tooltip("Reject quads covering more than this fraction of the frame; at that size the " +
                 "tag fills the view and is almost certainly the frame border, not a marker.")]
        [Range(0.05f, 1f)] public float MaxAreaFraction = 0.9f;

        [Tooltip("Douglas-Peucker tolerance as a fraction of contour perimeter when reducing a " +
                 "traced outline to a quad.")]
        [Range(0.01f, 0.15f)] public float PolygonToleranceFraction = 0.045f;

        [Tooltip("Fraction of the black quiet border that may read as white before a candidate " +
                 "is thrown out. OpenCV's default, and the main thing that rejects the thousands " +
                 "of dark rectangles in a real room.")]
        [Range(0f, 0.6f)] public float MaxBorderErrorRate = 0.35f;

        [Tooltip("Fit each marker edge to sub-pixel accuracy before solving the pose. Worth the " +
                 "cost: whole-pixel corners on a tag 100 px across are around a degree of " +
                 "orientation error, which is centimetres out at the end of a UR3e's reach.")]
        public bool RefineCorners = true;

        [Tooltip("Discard a detection whose contrast between white and black cells is below " +
                 "this. Guards against 'decoded' tags that are really noise in a dark corner.")]
        [Range(0f, 0.5f)] public float MinContrast = 0.10f;
    }

    // A self-contained ArUco detector: grayscale image in, identified markers with sub-pixel
    // corners out. No OpenCV, no native plugin, nothing that would not survive the Quest APK
    // build - which is the whole reason it exists rather than being one call into a library.
    //
    // The pipeline is the standard one (OpenCV's aruco module follows the same shape):
    //   adaptive threshold -> trace dark region outlines -> keep convex quads ->
    //   perspective-unwarp each quad to a canonical grid -> read the cells ->
    //   look the code up in the dictionary (all four rotations) -> refine the corners.
    //
    // Deliberately allocation-light and free of any UnityEngine API beyond Vector2 maths, so it
    // can be run on a worker thread while the headset keeps rendering. Detection on a 1280x960
    // frame is tens of milliseconds - fine at the couple of hertz a one-shot calibration needs,
    // and far too slow to sit in Update on the main thread.
    public class ArucoDetector
    {
        // 8-neighbour offsets in clockwise order, starting east. Moore boundary tracing walks
        // this ring, so the order matters: consecutive entries must be adjacent directions.
        private static readonly int[] k_NeighbourX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] k_NeighbourY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        // Sample grid per cell when reading the marker's bits, and how much of each cell's edge
        // to ignore. The margin matters more than the sample count: cell boundaries are exactly
        // where unwarping error and blur put a black cell's ink into a white cell's average.
        private const int k_CellSamples = 4;
        private const float k_CellIgnoreMargin = 0.16f;

        // Perpendicular search range, in pixels, when refining an edge to sub-pixel accuracy
        private const float k_EdgeSearchRadius = 4f;
        private const float k_EdgeSampleStep = 0.5f;
        private const int k_EdgeSamplesPerSide = 24;

        private readonly ArucoDetectorSettings _settings;
        private readonly ArucoDictionary _dictionary;

        // Working buffers, sized to the frame and reused. Detection runs repeatedly for up to
        // ten seconds per calibration attempt; re-allocating a few megabytes each pass would put
        // that straight into the GC.
        private int _width;
        private int _height;
        private long[] _integral;
        private bool[] _dark;
        private bool[] _traced;
        private readonly List<int> _contour = new List<int>(4096);
        private readonly List<Vector2> _polygon = new List<Vector2>(64);
        private readonly List<ArucoDetection> _results = new List<ArucoDetection>(8);
        private float[] _cellMeans;
        private readonly List<Vector2> _edgePoints = new List<Vector2>(k_EdgeSamplesPerSide);

        public ArucoDetector(ArucoDetectorSettings settings)
        {
            _settings = settings ?? new ArucoDetectorSettings();
            _dictionary = ArucoDictionary.Get(_settings.Dictionary);
        }

        public ArucoDictionary Dictionary => _dictionary;

        // How far the pipeline got on the last frame, for a status line that can tell "I see
        // nothing" apart from "I see a quad I cannot read". Without this a failing calibration
        // gives the user nothing to act on.
        public int LastCandidateCount { get; private set; }
        public int LastQuadCount { get; private set; }

        // Quads whose code matched nothing as seen, but would have matched had the image not
        // been mirrored. Diagnosis only - see ArucoDictionary.IdentifiesWhenMirrored. A feed
        // flipped the wrong way finds squares and decodes none of them, which otherwise reads
        // as "your tag is from the wrong dictionary".
        public int LastMirroredCount { get; private set; }

        // gray is one byte per pixel, row-major, top row first. Returns markers found in this
        // frame; the returned list is reused between calls, so copy anything you keep.
        public List<ArucoDetection> Detect(byte[] gray, int width, int height)
        {
            _results.Clear();
            LastCandidateCount = 0;
            LastQuadCount = 0;
            LastMirroredCount = 0;

            if (gray == null || width <= 0 || height <= 0 || gray.Length < width * height)
                return _results;

            EnsureBuffers(width, height);
            Binarize(gray);

            int total = _dictionary.GridSize + 2;
            int cellCount = total * total;
            if (_cellMeans == null || _cellMeans.Length != cellCount)
                _cellMeans = new float[cellCount];

            float maxArea = _settings.MaxAreaFraction * width * height;
            float minSide = _settings.MinSideLengthPixels;
            // A quad of side `minSide` has a perimeter of 4x that; allow a wide margin below it
            // so the cheap length test never rejects a marker the real tests would have kept
            float minPerimeter = 3f * minSide;

            Array.Clear(_traced, 0, _traced.Length);

            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int index = row + x;
                    // Start only where a dark run begins, which is where an outer boundary is
                    if (!_dark[index] || _dark[index - 1] || _traced[index])
                        continue;

                    if (!TraceContour(x, y))
                        continue;

                    if (_contour.Count < minPerimeter)
                        continue;

                    LastCandidateCount++;

                    if (!ExtractQuad(maxArea, minSide))
                        continue;

                    LastQuadCount++;

                    if (TryIdentify(gray, out var detection))
                        _results.Add(detection);
                }
            }

            RemoveDuplicates();
            return _results;
        }

        private void EnsureBuffers(int width, int height)
        {
            if (_width == width && _height == height && _integral != null)
                return;

            _width = width;
            _height = height;
            // One extra row and column of zeros so the box sum needs no bounds special-casing
            _integral = new long[(width + 1) * (height + 1)];
            _dark = new bool[width * height];
            _traced = new bool[width * height];
        }

        // Adaptive mean threshold via a summed-area table: one pass to build, one lookup per
        // pixel regardless of window size. A global threshold is useless here - a tag half in
        // sunlight and half in shadow is the normal case in a room, not the exception.
        private void Binarize(byte[] gray)
        {
            int w = _width, h = _height;
            int stride = w + 1;

            for (int y = 0; y < h; y++)
            {
                long rowSum = 0;
                int src = y * w;
                int dst = (y + 1) * stride + 1;
                for (int x = 0; x < w; x++)
                {
                    rowSum += gray[src + x];
                    _integral[dst + x] = _integral[dst + x - stride] + rowSum;
                }
            }

            int radius = _settings.ThresholdWindowRadius;
            int offset = _settings.ThresholdOffset;

            for (int y = 0; y < h; y++)
            {
                int y0 = y - radius; if (y0 < 0) y0 = 0;
                int y1 = y + radius; if (y1 > h - 1) y1 = h - 1;
                int rowTop = y0 * stride;
                int rowBottom = (y1 + 1) * stride;
                int src = y * w;

                for (int x = 0; x < w; x++)
                {
                    int x0 = x - radius; if (x0 < 0) x0 = 0;
                    int x1 = x + radius; if (x1 > w - 1) x1 = w - 1;

                    long sum = _integral[rowBottom + x1 + 1] - _integral[rowBottom + x0]
                             - _integral[rowTop + x1 + 1] + _integral[rowTop + x0];
                    int area = (y1 - y0 + 1) * (x1 - x0 + 1);
                    int mean = (int)(sum / area);

                    _dark[src + x] = gray[src + x] < mean - offset;
                }
            }
        }

        // Moore boundary tracing: walk the outside of one dark blob, marking as we go so the
        // scan does not re-trace the same shape from every row it spans.
        private bool TraceContour(int startX, int startY)
        {
            _contour.Clear();

            int w = _width, h = _height;
            int start = startY * w + startX;
            int px = startX, py = startY;

            // We entered from the left, which is background, so that is where the neighbour
            // search starts from
            int backtrack = 4;
            int maxSteps = 4 * (w + h);

            for (int step = 0; step < maxSteps; step++)
            {
                _contour.Add(py * w + px);
                _traced[py * w + px] = true;

                int foundDirection = -1;
                int nx = 0, ny = 0;
                for (int k = 1; k <= 8; k++)
                {
                    int direction = (backtrack + k) & 7;
                    int cx = px + k_NeighbourX[direction];
                    int cy = py + k_NeighbourY[direction];
                    if (cx < 0 || cy < 0 || cx >= w || cy >= h)
                        continue;
                    if (!_dark[cy * w + cx])
                        continue;

                    foundDirection = direction;
                    nx = cx;
                    ny = cy;
                    break;
                }

                // An isolated pixel has no dark neighbour at all
                if (foundDirection < 0)
                    return false;

                px = nx;
                py = ny;
                backtrack = (foundDirection + 4) & 7;

                if (px == startX && py == startY && _contour.Count >= 3)
                    return true;
            }

            // Ran out of budget: an enormous or pathological blob, not a marker
            return false;
        }

        // Reduce the traced outline to four corners, or fail. Fills _polygon clockwise in image
        // space (x right, y down) on success.
        private bool ExtractQuad(float maxArea, float minSide)
        {
            _polygon.Clear();

            int count = _contour.Count;
            if (count < 8)
                return false;

            // Douglas-Peucker needs two anchors on the outline. The point furthest from the
            // first contour point, and the point furthest from that, are guaranteed to be far
            // apart on any shape - a fixed pair of indices would collapse on a diamond.
            Vector2 first = PointAt(0);
            int anchorA = FurthestFrom(first, 0, count);
            int anchorB = FurthestFrom(PointAt(anchorA), 0, count);
            if (anchorA == anchorB)
                return false;

            int lo = Mathf.Min(anchorA, anchorB);
            int hi = Mathf.Max(anchorA, anchorB);

            float perimeter = 0f;
            for (int i = 0; i < count; i++)
                perimeter += (PointAt((i + 1) % count) - PointAt(i)).magnitude;

            float tolerance = Mathf.Max(1f, _settings.PolygonToleranceFraction * perimeter);

            _polygon.Add(PointAt(lo));
            Simplify(lo, hi, tolerance);
            _polygon.Add(PointAt(hi));
            Simplify(hi, count + lo, tolerance);

            if (_polygon.Count != 4)
                return false;

            if (!IsConvex(_polygon))
                return false;

            // Signed area in image coordinates (y down): positive means the vertices run
            // clockwise on screen, which is the order the bit reader and the dictionary rotation
            // convention both assume
            float area = SignedArea(_polygon);
            if (area < 0f)
            {
                var swap = _polygon[1];
                _polygon[1] = _polygon[3];
                _polygon[3] = swap;
                area = -area;
            }

            if (area > maxArea)
                return false;

            for (int i = 0; i < 4; i++)
            {
                if ((_polygon[(i + 1) & 3] - _polygon[i]).magnitude < minSide)
                    return false;
            }

            return true;
        }

        // Douglas-Peucker over the closed contour between two indices, appending the kept
        // vertices to _polygon in order. Bails out once the polygon is over-full, since anything
        // with more than four corners is not a marker and the recursion is pure cost from there.
        private void Simplify(int from, int to, float tolerance)
        {
            if (_polygon.Count > 4 || to - from < 2)
                return;

            int count = _contour.Count;
            Vector2 a = PointAt(from % count);
            Vector2 b = PointAt(to % count);
            Vector2 edge = b - a;
            float edgeLength = edge.magnitude;

            float worst = -1f;
            int worstIndex = -1;

            for (int i = from + 1; i < to; i++)
            {
                Vector2 p = PointAt(i % count);
                float distance = edgeLength < 1e-5f
                    ? (p - a).magnitude
                    : Mathf.Abs(edge.x * (a.y - p.y) - (a.x - p.x) * edge.y) / edgeLength;

                if (distance <= worst)
                    continue;

                worst = distance;
                worstIndex = i;
            }

            if (worstIndex < 0 || worst < tolerance)
                return;

            Simplify(from, worstIndex, tolerance);
            _polygon.Add(PointAt(worstIndex % count));
            Simplify(worstIndex, to, tolerance);
        }

        private int FurthestFrom(Vector2 origin, int from, int count)
        {
            int best = from;
            float bestDistance = -1f;
            for (int i = from; i < count; i++)
            {
                float distance = (PointAt(i) - origin).sqrMagnitude;
                if (distance <= bestDistance)
                    continue;
                bestDistance = distance;
                best = i;
            }
            return best;
        }

        private Vector2 PointAt(int contourIndex)
        {
            int index = _contour[contourIndex];
            return new Vector2(index % _width, index / _width);
        }

        private static bool IsConvex(List<Vector2> polygon)
        {
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) & 3];
                Vector2 c = polygon[(i + 2) & 3];
                float cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
                int current = cross > 0f ? 1 : (cross < 0f ? -1 : 0);
                if (current == 0)
                    return false;
                if (sign == 0)
                    sign = current;
                else if (sign != current)
                    return false;
            }
            return true;
        }

        private static float SignedArea(List<Vector2> polygon)
        {
            float sum = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                sum += a.x * b.y - b.x * a.y;
            }
            return 0.5f * sum;
        }

        // Unwarp the quad onto a canonical (grid + border) cell image, read the cells, and look
        // the code up. Fails for the overwhelming majority of quads in a room, which is the
        // point - the black border test alone throws out nearly all of them.
        private bool TryIdentify(byte[] gray, out ArucoDetection detection)
        {
            detection = default;

            var h = Homography.FromUnitSquare(_polygon[0], _polygon[1], _polygon[2], _polygon[3]);
            if (!h.IsValid)
                return false;

            int grid = _dictionary.GridSize;
            int total = grid + 2;

            for (int row = 0; row < total; row++)
            {
                for (int column = 0; column < total; column++)
                    _cellMeans[row * total + column] = SampleCell(gray, h, row, column, total);
            }

            float threshold = OtsuThreshold(_cellMeans);

            int borderCells = 0;
            int borderErrors = 0;
            for (int row = 0; row < total; row++)
            {
                for (int column = 0; column < total; column++)
                {
                    if (row != 0 && row != total - 1 && column != 0 && column != total - 1)
                        continue;
                    borderCells++;
                    if (_cellMeans[row * total + column] > threshold)
                        borderErrors++;
                }
            }

            if (borderErrors > _settings.MaxBorderErrorRate * borderCells)
                return false;

            ulong observed = 0UL;
            float whiteSum = 0f, blackSum = 0f;
            int whiteCount = 0, blackCount = 0;

            for (int row = 0; row < grid; row++)
            {
                for (int column = 0; column < grid; column++)
                {
                    float value = _cellMeans[(row + 1) * total + (column + 1)];
                    bool white = value > threshold;
                    observed = (observed << 1) | (white ? 1UL : 0UL);

                    if (white)
                    {
                        whiteSum += value;
                        whiteCount++;
                    }
                    else
                    {
                        blackSum += value;
                        blackCount++;
                    }
                }
            }

            // An all-white or all-black inner grid is not a marker in any dictionary, and would
            // make the contrast figure meaningless
            if (whiteCount == 0 || blackCount == 0)
                return false;

            float contrast = (whiteSum / whiteCount - blackSum / blackCount) / 255f;
            if (contrast < _settings.MinContrast)
                return false;

            if (!_dictionary.TryIdentify(observed, out int id, out int rotation, out int errors))
            {
                if (_dictionary.IdentifiesWhenMirrored(observed))
                    LastMirroredCount++;
                return false;
            }

            // Put the corners into the marker's own frame: printed corner j is the observed
            // corner (j - rotation) mod 4. Skipping this yields the right id at a pose yawed by
            // a multiple of 90 degrees.
            var corners = new Vector2[4];
            for (int j = 0; j < 4; j++)
                corners[j] = _polygon[((j - rotation) + 4) & 3];

            if (_settings.RefineCorners)
                RefineQuad(gray, corners);

            // Everything above works in array-index space, where sample (x, y) sits at integer
            // (x, y). The pinhole model the pose solver uses puts the CENTRE of that same pixel
            // at (x + 0.5, y + 0.5) - that is what cx and cy are measured against. Half a pixel
            // is not nothing here: left uncorrected it is a fixed bias on every corner, which the
            // solver turns into a small but systematic lean on every pose.
            for (int j = 0; j < 4; j++)
                corners[j] += new Vector2(0.5f, 0.5f);

            detection = new ArucoDetection
            {
                Id = id,
                Corners = corners,
                BitErrors = errors,
                Contrast = contrast,
            };
            return true;
        }

        private float SampleCell(byte[] gray, Homography h, int row, int column, int total)
        {
            float sum = 0f;
            int taken = 0;
            float span = 1f - 2f * k_CellIgnoreMargin;

            for (int sy = 0; sy < k_CellSamples; sy++)
            {
                float v = (row + k_CellIgnoreMargin + span * (sy + 0.5f) / k_CellSamples) / total;
                for (int sx = 0; sx < k_CellSamples; sx++)
                {
                    float u = (column + k_CellIgnoreMargin + span * (sx + 0.5f) / k_CellSamples) / total;
                    Vector2 p = h.Map(u, v);
                    if (!TrySampleBilinear(gray, p.x, p.y, out float value))
                        continue;
                    sum += value;
                    taken++;
                }
            }

            // A cell that fell outside the frame reads as black, which fails the border test and
            // rejects the candidate - the right outcome for a marker running off the edge
            return taken == 0 ? 0f : sum / taken;
        }

        private bool TrySampleBilinear(byte[] gray, float x, float y, out float value)
        {
            value = 0f;
            if (x < 0f || y < 0f || x > _width - 1.001f || y > _height - 1.001f)
                return false;

            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int index = y0 * _width + x0;

            float top = gray[index] * (1f - fx) + gray[index + 1] * fx;
            float bottom = gray[index + _width] * (1f - fx) + gray[index + _width + 1] * fx;
            value = top * (1f - fy) + bottom * fy;
            return true;
        }

        // Otsu over the per-cell means rather than over raw pixels: the cells are what is being
        // classified, and a handful of well-separated values give a far cleaner split than the
        // hundreds of thousands of edge pixels between them.
        private static float OtsuThreshold(float[] values)
        {
            Span<int> histogram = stackalloc int[256];
            for (int i = 0; i < values.Length; i++)
            {
                int bin = (int)values[i];
                if (bin < 0) bin = 0;
                else if (bin > 255) bin = 255;
                histogram[bin]++;
            }

            int total = values.Length;
            float sum = 0f;
            for (int i = 0; i < 256; i++)
                sum += i * histogram[i];

            float sumBackground = 0f;
            int weightBackground = 0;
            float bestVariance = -1f;
            int bestThreshold = 128;

            for (int t = 0; t < 256; t++)
            {
                weightBackground += histogram[t];
                if (weightBackground == 0)
                    continue;

                int weightForeground = total - weightBackground;
                if (weightForeground == 0)
                    break;

                sumBackground += t * histogram[t];
                float meanBackground = sumBackground / weightBackground;
                float meanForeground = (sum - sumBackground) / weightForeground;
                float difference = meanBackground - meanForeground;
                float variance = weightBackground * (float)weightForeground * difference * difference;

                if (variance <= bestVariance)
                    continue;

                bestVariance = variance;
                bestThreshold = t;
            }

            return bestThreshold;
        }

        // Fit each side of the marker to the underlying grey-level edge and re-intersect, which
        // moves the corners from whole-pixel contour vertices to a fraction of a pixel.
        //
        // Fitting the SIDES rather than nudging the corners is what makes this work: a corner
        // sits where the image gradient is ambiguous in both directions, while a side has a long
        // straight run of unambiguous gradient to average over. Corners that fail to improve are
        // left exactly as they were.
        private void RefineQuad(byte[] gray, Vector2[] corners)
        {
            Span<Vector3> lines = stackalloc Vector3[4];

            for (int i = 0; i < 4; i++)
            {
                if (!TryFitEdge(gray, corners[i], corners[(i + 1) & 3], out Vector3 line))
                    return;
                lines[i] = line;
            }

            var refined = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                // Corner i is where the edge arriving at it and the edge leaving it meet
                if (!TryIntersect(lines[(i + 3) & 3], lines[i], out Vector2 point))
                    return;

                // A refinement that moves a corner further than this is a fit that latched onto
                // something else in the scene, not a better estimate of the same corner
                if ((point - corners[i]).sqrMagnitude > 9f)
                    return;

                refined[i] = point;
            }

            for (int i = 0; i < 4; i++)
                corners[i] = refined[i];
        }

        // Returns the edge as a line (a, b, c) with a*x + b*y + c = 0 and a^2 + b^2 = 1
        private bool TryFitEdge(byte[] gray, Vector2 from, Vector2 to, out Vector3 line)
        {
            line = Vector3.zero;
            _edgePoints.Clear();

            Vector2 along = to - from;
            float length = along.magnitude;
            if (length < 8f)
                return false;

            along /= length;
            Vector2 normal = new Vector2(-along.y, along.x);

            for (int i = 0; i < k_EdgeSamplesPerSide; i++)
            {
                // Stay clear of the corners themselves, where the neighbouring edge's gradient
                // bleeds into the perpendicular search
                float t = 0.15f + 0.70f * (i + 0.5f) / k_EdgeSamplesPerSide;
                Vector2 basePoint = from + along * (t * length);

                if (TryFindEdgeOffset(gray, basePoint, normal, out float offset))
                    _edgePoints.Add(basePoint + normal * offset);
            }

            if (_edgePoints.Count < 6)
                return false;

            if (!FitLine(_edgePoints, out line))
                return false;

            // One robust pass: drop the points that disagree with the first fit and refit. A
            // single occluding object across one side of the tag is otherwise enough to drag the
            // whole edge with it.
            float median = MedianAbsoluteResidual(_edgePoints, line);
            float cutoff = Mathf.Max(0.75f, 2.5f * median);

            int kept = 0;
            for (int i = 0; i < _edgePoints.Count; i++)
            {
                float residual = Mathf.Abs(line.x * _edgePoints[i].x + line.y * _edgePoints[i].y + line.z);
                if (residual > cutoff)
                    continue;
                _edgePoints[kept++] = _edgePoints[i];
            }

            if (kept < 6)
                return true; // the first fit stands

            _edgePoints.RemoveRange(kept, _edgePoints.Count - kept);
            return FitLine(_edgePoints, out line);
        }

        // Walk perpendicular to the edge looking for the steepest intensity change, then
        // interpolate between the three samples around it for a sub-pixel position.
        private bool TryFindEdgeOffset(byte[] gray, Vector2 origin, Vector2 normal, out float offset)
        {
            offset = 0f;

            int steps = Mathf.RoundToInt(2f * k_EdgeSearchRadius / k_EdgeSampleStep) + 1;
            Span<float> samples = stackalloc float[steps];

            for (int i = 0; i < steps; i++)
            {
                float d = -k_EdgeSearchRadius + i * k_EdgeSampleStep;
                Vector2 p = origin + normal * d;
                if (!TrySampleBilinear(gray, p.x, p.y, out samples[i]))
                    return false;
            }

            float bestGradient = 0f;
            int bestIndex = -1;
            for (int i = 1; i < steps - 1; i++)
            {
                float gradient = Mathf.Abs(samples[i + 1] - samples[i - 1]);
                if (gradient <= bestGradient)
                    continue;
                bestGradient = gradient;
                bestIndex = i;
            }

            // Too flat to be an edge: the tag is blurred past usefulness here, or something is
            // occluding this stretch of the side
            if (bestIndex < 1 || bestGradient < 8f)
                return false;

            float g0 = Mathf.Abs(samples[bestIndex] - samples[bestIndex - 1]);
            float g1 = Mathf.Abs(samples[bestIndex + 1] - samples[bestIndex]);
            float denominator = g0 + g1;
            float shift = denominator > 1e-4f ? (g1 - g0) / (2f * denominator) : 0f;

            offset = -k_EdgeSearchRadius + (bestIndex + shift) * k_EdgeSampleStep;
            return true;
        }

        // Total least squares: the line through the centroid along the points' principal axis.
        // Ordinary least squares would blow up on the two sides of the marker that are close to
        // vertical in the image, which is half of them.
        private static bool FitLine(List<Vector2> points, out Vector3 line)
        {
            line = Vector3.zero;
            int n = points.Count;
            if (n < 2)
                return false;

            Vector2 mean = Vector2.zero;
            for (int i = 0; i < n; i++)
                mean += points[i];
            mean /= n;

            float sxx = 0f, sxy = 0f, syy = 0f;
            for (int i = 0; i < n; i++)
            {
                float dx = points[i].x - mean.x;
                float dy = points[i].y - mean.y;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
            }

            // Normal direction is the eigenvector of the smaller eigenvalue of [[sxx,sxy],[sxy,syy]]
            float difference = sxx - syy;
            float root = Mathf.Sqrt(difference * difference + 4f * sxy * sxy);
            float smallest = 0.5f * (sxx + syy - root);

            Vector2 normal = new Vector2(sxy, smallest - sxx);
            if (normal.sqrMagnitude < 1e-12f)
                normal = new Vector2(smallest - syy, sxy);
            if (normal.sqrMagnitude < 1e-12f)
                return false;

            normal.Normalize();
            line = new Vector3(normal.x, normal.y, -(normal.x * mean.x + normal.y * mean.y));
            return true;
        }

        private static float MedianAbsoluteResidual(List<Vector2> points, Vector3 line)
        {
            int n = points.Count;
            Span<float> residuals = n <= 64 ? stackalloc float[n] : new float[n];
            for (int i = 0; i < n; i++)
                residuals[i] = Mathf.Abs(line.x * points[i].x + line.y * points[i].y + line.z);

            // n is at most a couple of dozen; an insertion sort beats allocating to sort it
            for (int i = 1; i < n; i++)
            {
                float value = residuals[i];
                int j = i - 1;
                while (j >= 0 && residuals[j] > value)
                {
                    residuals[j + 1] = residuals[j];
                    j--;
                }
                residuals[j + 1] = value;
            }

            return residuals[n / 2];
        }

        private static bool TryIntersect(Vector3 first, Vector3 second, out Vector2 point)
        {
            point = Vector2.zero;
            float determinant = first.x * second.y - first.y * second.x;
            // Near-parallel sides mean the quad has collapsed; the unrefined corners are better
            // than an intersection thrown off to infinity
            if (Mathf.Abs(determinant) < 1e-4f)
                return false;

            point = new Vector2(
                (first.y * second.z - first.z * second.y) / determinant,
                (first.z * second.x - first.x * second.z) / determinant);
            return true;
        }

        // The outer and inner boundaries of the marker's black border both trace as quads, so
        // the same tag is routinely found twice. Keep the reading with fewer bit errors.
        private void RemoveDuplicates()
        {
            for (int i = 0; i < _results.Count; i++)
            {
                for (int j = i + 1; j < _results.Count; j++)
                {
                    var a = _results[i];
                    var b = _results[j];

                    float separation = (a.Center - b.Center).magnitude;
                    float scale = Mathf.Min(a.MinSideLength, b.MinSideLength);
                    if (separation > 0.4f * scale)
                        continue;

                    bool keepFirst = a.BitErrors < b.BitErrors
                        || (a.BitErrors == b.BitErrors && a.MinSideLength >= b.MinSideLength);

                    if (keepFirst)
                        _results.RemoveAt(j);
                    else
                        _results.RemoveAt(i);

                    i--;
                    break;
                }
            }
        }
    }

    // Projective map from the unit square to a quad, in the closed form for that special case -
    // no linear solver required, which keeps the detector free of a matrix library.
    public struct Homography
    {
        public float A, B, C, D, E, F, G, H;
        public bool IsValid;

        // Maps (0,0) -> p0, (1,0) -> p1, (1,1) -> p2, (0,1) -> p3
        public static Homography FromUnitSquare(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            var result = new Homography();

            float dx1 = p1.x - p2.x;
            float dx2 = p3.x - p2.x;
            float dx3 = p0.x - p1.x + p2.x - p3.x;
            float dy1 = p1.y - p2.y;
            float dy2 = p3.y - p2.y;
            float dy3 = p0.y - p1.y + p2.y - p3.y;

            if (Mathf.Abs(dx3) < 1e-6f && Mathf.Abs(dy3) < 1e-6f)
            {
                // The quad is a parallelogram: the map is affine and the projective terms vanish
                result.A = p1.x - p0.x;
                result.B = p2.x - p1.x;
                result.C = p0.x;
                result.D = p1.y - p0.y;
                result.E = p2.y - p1.y;
                result.F = p0.y;
                result.G = 0f;
                result.H = 0f;
                result.IsValid = true;
                return result;
            }

            float determinant = dx1 * dy2 - dy1 * dx2;
            if (Mathf.Abs(determinant) < 1e-9f)
                return result; // IsValid stays false

            result.G = (dx3 * dy2 - dy3 * dx2) / determinant;
            result.H = (dx1 * dy3 - dy1 * dx3) / determinant;
            result.A = p1.x - p0.x + result.G * p1.x;
            result.B = p3.x - p0.x + result.H * p3.x;
            result.C = p0.x;
            result.D = p1.y - p0.y + result.G * p1.y;
            result.E = p3.y - p0.y + result.H * p3.y;
            result.F = p0.y;
            result.IsValid = true;
            return result;
        }

        public Vector2 Map(float u, float v)
        {
            float w = G * u + H * v + 1f;
            if (Mathf.Abs(w) < 1e-9f)
                return Vector2.zero;
            return new Vector2((A * u + B * v + C) / w, (D * u + E * v + F) / w);
        }
    }
}
