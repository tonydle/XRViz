using System;
using UnityEngine;

namespace Unity.Robotics
{
    // Pinhole intrinsics in pixels, for the resolution the image was actually detected at.
    // Named for the model rather than for "camera" because OVRPlugin has a CameraIntrinsics of
    // its own that means something else.
    [Serializable]
    public struct PinholeIntrinsics
    {
        public float Fx;
        public float Fy;
        public float Cx;
        public float Cy;
        public int Width;
        public int Height;

        public bool IsValid => Fx > 1f && Fy > 1f && Width > 0 && Height > 0;

        // Rescale to a different image size. The passthrough camera reports its intrinsics
        // against the sensor's full active array, which is not necessarily the resolution the
        // WebCamTexture hands over - and using them unscaled puts the principal point in the
        // wrong place, which tilts every pose.
        public PinholeIntrinsics ScaledTo(int width, int height)
        {
            if (Width <= 0 || Height <= 0)
                return this;

            float sx = width / (float)Width;
            float sy = height / (float)Height;
            return new PinholeIntrinsics
            {
                Fx = Fx * sx,
                Fy = Fy * sy,
                Cx = Cx * sx,
                Cy = Cy * sy,
                Width = width,
                Height = height,
            };
        }

        // Horizontal field of view in degrees - only used for reporting, but it is the number a
        // human can sanity-check an intrinsics guess against
        public float HorizontalFovDegrees =>
            Fx > 1f ? 2f * Mathf.Atan(0.5f * Width / Fx) * Mathf.Rad2Deg : 0f;

        public override string ToString() =>
            $"fx={Fx:F1} fy={Fy:F1} cx={Cx:F1} cy={Cy:F1} @ {Width}x{Height} ({HorizontalFovDegrees:F0} deg HFOV)";
    }

    // A marker's pose relative to the camera, in Unity's camera-local axes.
    public struct MarkerPose
    {
        public bool Valid;

        // Metres, in the Unity camera's local space (x right, y up, z forward)
        public Vector3 Position;

        // Marker +Z points OUT of the printed face, +Y points to the top of the printed image
        public Quaternion Rotation;

        // RMS distance in pixels between the observed corners and where this pose puts them.
        // Under about a pixel is a good read; several pixels means the corners or the intrinsics
        // are wrong and the pose should not be trusted whatever it looks like.
        public float ReprojectionErrorPixels;

        // Best error divided by the runner-up's. A single planar marker has two poses that
        // project almost identically when viewed head-on; near 1.0 means the two fit equally
        // well and the tilt of this pose is a coin toss. Yaw survives it, tilt does not.
        public float AmbiguityRatio;
    }

    // Recovers a marker's 6-DoF pose from its four image corners and the camera intrinsics.
    //
    // Two stages, both standard: a closed-form guess from the marker-plane-to-image homography,
    // then Gauss-Newton on the reprojection error to clean it up. The guess alone is noticeably
    // biased - it makes no use of the fact that the four corners are equally reliable - and the
    // refinement typically takes the residual from a couple of pixels to a fraction of one.
    //
    // Both solutions of the planar ambiguity are refined and the better one returned, with
    // AmbiguityRatio saying how much better. The calibration flow leans on this: it levels the
    // final pose to horizontal and averages over a whole search window from a moving headset,
    // which is exactly what the ambiguity cannot survive.
    public static class MarkerPoseSolver
    {
        private const int k_MaxIterations = 12;
        private const float k_ConvergenceEpsilon = 1e-7f;

        // markerSizeMetres is the side length of the black square, border included - the same
        // number OpenCV and every marker generator mean by "marker size".
        public static MarkerPose Solve(Vector2[] corners, float markerSizeMetres,
            PinholeIntrinsics intrinsics)
        {
            var result = new MarkerPose { Valid = false };

            if (corners == null || corners.Length != 4 || markerSizeMetres <= 0f
                || !intrinsics.IsValid)
                return result;

            float half = 0.5f * markerSizeMetres;

            // Marker frame, OpenCV's convention: origin at the centre, X right, Y up, Z out of
            // the face. Corner order matches ArucoDetection: top-left, top-right, bottom-right,
            // bottom-left as printed.
            Span<Vector3> objectPoints = stackalloc Vector3[4];
            objectPoints[0] = new Vector3(-half, half, 0f);
            objectPoints[1] = new Vector3(half, half, 0f);
            objectPoints[2] = new Vector3(half, -half, 0f);
            objectPoints[3] = new Vector3(-half, -half, 0f);

            if (!TryInitialPose(corners, half, intrinsics, out Matrix4x4 rotation,
                    out Vector3 translation))
                return result;

            // Second solution of the planar ambiguity: the same marker tilted the other way
            // about the viewing ray. Reflecting the plane normal in the ray and rebuilding the
            // frame gets close enough for the refinement to fall into that basin if it is real.
            Matrix4x4 alternateRotation = MirrorAboutViewingRay(rotation, translation);

            float errorA = Refine(objectPoints, corners, intrinsics, ref rotation, ref translation);

            Vector3 alternateTranslation = translation;
            float errorB = Refine(objectPoints, corners, intrinsics, ref alternateRotation,
                ref alternateTranslation);

            bool preferAlternate = errorB < errorA;
            Matrix4x4 bestRotation = preferAlternate ? alternateRotation : rotation;
            Vector3 bestTranslation = preferAlternate ? alternateTranslation : translation;
            float bestError = preferAlternate ? errorB : errorA;
            float worstError = preferAlternate ? errorA : errorB;

            // Behind the camera is not a pose, it is a failed solve
            if (bestTranslation.z <= 0f || float.IsNaN(bestError) || float.IsInfinity(bestError))
                return result;

            result.Valid = true;
            result.ReprojectionErrorPixels = bestError;
            result.AmbiguityRatio = worstError > 1e-6f
                ? Mathf.Clamp01(bestError / worstError)
                : 1f;

            ToUnity(bestRotation, bestTranslation, out result.Position, out result.Rotation);
            return result;
        }

        // Homography from the marker plane to normalised image coordinates, decomposed into the
        // rotation and translation it implies. Zhang's method, in the form where the source is a
        // square so the homography has a closed form and no linear solver is needed.
        private static bool TryInitialPose(Vector2[] corners, float half,
            PinholeIntrinsics intrinsics, out Matrix4x4 rotation, out Vector3 translation)
        {
            rotation = Matrix4x4.identity;
            translation = Vector3.zero;

            // Undo the intrinsics, so the homography maps straight onto the normalised camera
            // plane and its columns are the rotation columns up to one common scale
            Span<Vector2> normalised = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                normalised[i] = new Vector2(
                    (corners[i].x - intrinsics.Cx) / intrinsics.Fx,
                    (corners[i].y - intrinsics.Cy) / intrinsics.Fy);
            }

            var h = Homography.FromUnitSquare(normalised[0], normalised[1], normalised[2],
                normalised[3]);
            if (!h.IsValid)
                return false;

            // FromUnitSquare maps (u,v) with v running DOWN the marker (0 at the top edge).
            // Marker coordinates run (X right, Y up) from the centre, so u = X/(2*half) + 1/2 and
            // v = 1/2 - Y/(2*half). Substituting that change of variables into the projective map
            // gives the columns below directly, avoiding a second matrix multiply.
            float scale = 1f / (2f * half);

            // Column for X: coefficients of X in numerator and denominator
            Vector3 hx = new Vector3(h.A * scale, h.D * scale, h.G * scale);
            // Column for Y: v decreases as Y increases, hence the negated sign
            Vector3 hy = new Vector3(-h.B * scale, -h.E * scale, -h.H * scale);
            // Column for 1: the map evaluated at the marker centre (u = v = 1/2)
            Vector3 hw = new Vector3(
                h.C + 0.5f * (h.A + h.B),
                h.F + 0.5f * (h.D + h.E),
                1f + 0.5f * (h.G + h.H));

            float magnitudeX = hx.magnitude;
            float magnitudeY = hy.magnitude;
            if (magnitudeX < 1e-9f || magnitudeY < 1e-9f)
                return false;

            // The two in-plane rotation columns must be unit length, and the same scale applies
            // to the translation. Averaging the two is the usual guard against one of them being
            // the more foreshortened - and so noisier - of the pair.
            float lambda = 2f / (magnitudeX + magnitudeY);

            // The marker is in front of the camera, so the scale sign is the one that puts it there
            if (hw.z < 0f)
                lambda = -lambda;

            Vector3 r1 = hx * lambda;
            Vector3 r2 = hy * lambda;
            translation = hw * lambda;

            if (translation.z <= 0f)
                return false;

            // The decomposition does not guarantee r1 and r2 are orthogonal; make them so before
            // completing the frame, or the "rotation" is a shear and the refinement starts lame
            Orthonormalise(ref r1, ref r2, out Vector3 r3);

            rotation = FromColumns(r1, r2, r3);
            return true;
        }

        // The other pose that projects to nearly the same quad: reflect the marker's normal in
        // the viewing ray, keeping the in-plane direction as close as possible.
        private static Matrix4x4 MirrorAboutViewingRay(Matrix4x4 rotation, Vector3 translation)
        {
            Vector3 ray = translation.normalized;
            if (ray.sqrMagnitude < 1e-12f)
                return rotation;

            Vector3 normal = Column(rotation, 2);
            Vector3 mirroredNormal = 2f * Vector3.Dot(normal, ray) * ray - normal;

            Vector3 r1 = Column(rotation, 0);
            // Project the old X axis onto the new plane so the in-plane rotation carries over
            r1 -= Vector3.Dot(r1, mirroredNormal) * mirroredNormal;
            if (r1.sqrMagnitude < 1e-12f)
                return rotation;

            r1.Normalize();
            Vector3 r2 = Vector3.Cross(mirroredNormal, r1);
            return FromColumns(r1, r2, mirroredNormal);
        }

        // Gauss-Newton with Levenberg-Marquardt damping on the 6-DoF pose, minimising squared
        // reprojection error in pixels. Returns the RMS pixel error of the pose it leaves behind.
        private static float Refine(Span<Vector3> objectPoints, Vector2[] corners,
            PinholeIntrinsics intrinsics, ref Matrix4x4 rotation, ref Vector3 translation)
        {
            float damping = 1e-3f;
            float error = Reprojection(objectPoints, corners, intrinsics, rotation, translation);

            Span<float> normal = stackalloc float[36];
            Span<float> gradient = stackalloc float[6];
            Span<float> step = stackalloc float[6];
            Span<float> rowU = stackalloc float[6];
            Span<float> rowV = stackalloc float[6];

            for (int iteration = 0; iteration < k_MaxIterations; iteration++)
            {
                normal.Clear();
                gradient.Clear();

                for (int i = 0; i < 4; i++)
                {
                    Vector3 rotated = rotation.MultiplyVector(objectPoints[i]);
                    Vector3 camera = rotated + translation;
                    if (camera.z <= 1e-6f)
                        return float.PositiveInfinity;

                    float inverseZ = 1f / camera.z;
                    float projectedU = intrinsics.Fx * camera.x * inverseZ + intrinsics.Cx;
                    float projectedV = intrinsics.Fy * camera.y * inverseZ + intrinsics.Cy;

                    float residualU = projectedU - corners[i].x;
                    float residualV = projectedV - corners[i].y;

                    // d(pixel)/d(camera point)
                    float duX = intrinsics.Fx * inverseZ;
                    float duZ = -intrinsics.Fx * camera.x * inverseZ * inverseZ;
                    float dvY = intrinsics.Fy * inverseZ;
                    float dvZ = -intrinsics.Fy * camera.y * inverseZ * inverseZ;

                    // A left-multiplied rotation increment exp([w]x) moves the camera point by
                    // -[R*P]x * w, so d(camera)/d(w) is the negated cross-product matrix of the
                    // rotated (not translated) object point r:
                    //     dQx/dw = ( 0,   rz, -ry)
                    //     dQy/dw = (-rz,  0,   rx)
                    //     dQz/dw = ( ry, -rx,  0 )
                    // contracted here with d(pixel)/d(camera) rather than built as a matrix
                    // product, to keep this allocation-free on a worker thread.
                    float rx = rotated.x, ry = rotated.y, rz = rotated.z;

                    rowU[0] = duX;
                    rowU[1] = 0f;
                    rowU[2] = duZ;
                    rowU[3] = duZ * ry;
                    rowU[4] = duX * rz - duZ * rx;
                    rowU[5] = -duX * ry;

                    rowV[0] = 0f;
                    rowV[1] = dvY;
                    rowV[2] = dvZ;
                    rowV[3] = -dvY * rz + dvZ * ry;
                    rowV[4] = -dvZ * rx;
                    rowV[5] = dvY * rx;

                    Accumulate(normal, gradient, rowU, residualU);
                    Accumulate(normal, gradient, rowV, residualV);
                }

                bool improved = false;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (!SolveDamped(normal, gradient, damping, step))
                    {
                        damping *= 10f;
                        continue;
                    }

                    Vector3 deltaTranslation = new Vector3(step[0], step[1], step[2]);
                    Vector3 deltaRotation = new Vector3(step[3], step[4], step[5]);

                    Matrix4x4 candidateRotation = ExpMap(deltaRotation) * rotation;
                    Vector3 candidateTranslation = translation + deltaTranslation;

                    float candidateError = Reprojection(objectPoints, corners, intrinsics,
                        candidateRotation, candidateTranslation);

                    if (candidateError < error)
                    {
                        float previousError = error;
                        rotation = candidateRotation;
                        translation = candidateTranslation;
                        error = candidateError;
                        damping = Mathf.Max(damping * 0.3f, 1e-9f);
                        improved = true;
                        if (previousError - candidateError < k_ConvergenceEpsilon)
                            return error;
                        break;
                    }

                    damping *= 10f;
                }

                if (!improved)
                    break;
            }

            return error;
        }

        private static void Accumulate(Span<float> normal, Span<float> gradient,
            Span<float> row, float residual)
        {
            for (int i = 0; i < 6; i++)
            {
                gradient[i] -= row[i] * residual;
                for (int j = 0; j < 6; j++)
                    normal[i * 6 + j] += row[i] * row[j];
            }
        }

        // (J'J + damping * diag(J'J)) * step = -J' * residual, by Gaussian elimination with
        // partial pivoting. Six unknowns; anything fancier would be more code than arithmetic.
        private static bool SolveDamped(Span<float> normal, Span<float> gradient, float damping,
            Span<float> step)
        {
            Span<float> matrix = stackalloc float[42]; // 6x6 augmented with the right-hand side

            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++)
                    matrix[i * 7 + j] = normal[i * 6 + j];

                matrix[i * 7 + i] += damping * Mathf.Max(normal[i * 6 + i], 1e-9f);
                matrix[i * 7 + 6] = gradient[i];
            }

            for (int column = 0; column < 6; column++)
            {
                int pivot = column;
                float best = Mathf.Abs(matrix[column * 7 + column]);
                for (int row = column + 1; row < 6; row++)
                {
                    float magnitude = Mathf.Abs(matrix[row * 7 + column]);
                    if (magnitude <= best)
                        continue;
                    best = magnitude;
                    pivot = row;
                }

                if (best < 1e-12f)
                    return false;

                if (pivot != column)
                {
                    for (int k = column; k < 7; k++)
                    {
                        float swap = matrix[column * 7 + k];
                        matrix[column * 7 + k] = matrix[pivot * 7 + k];
                        matrix[pivot * 7 + k] = swap;
                    }
                }

                float diagonal = matrix[column * 7 + column];
                for (int row = column + 1; row < 6; row++)
                {
                    float factor = matrix[row * 7 + column] / diagonal;
                    if (factor == 0f)
                        continue;
                    for (int k = column; k < 7; k++)
                        matrix[row * 7 + k] -= factor * matrix[column * 7 + k];
                }
            }

            for (int row = 5; row >= 0; row--)
            {
                float sum = matrix[row * 7 + 6];
                for (int k = row + 1; k < 6; k++)
                    sum -= matrix[row * 7 + k] * step[k];
                step[row] = sum / matrix[row * 7 + row];
            }

            for (int i = 0; i < 6; i++)
            {
                if (float.IsNaN(step[i]) || float.IsInfinity(step[i]))
                    return false;
            }

            return true;
        }

        private static float Reprojection(Span<Vector3> objectPoints, Vector2[] corners,
            PinholeIntrinsics intrinsics, Matrix4x4 rotation, Vector3 translation)
        {
            float sum = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 camera = rotation.MultiplyVector(objectPoints[i]) + translation;
                if (camera.z <= 1e-6f)
                    return float.PositiveInfinity;

                float u = intrinsics.Fx * camera.x / camera.z + intrinsics.Cx;
                float v = intrinsics.Fy * camera.y / camera.z + intrinsics.Cy;
                float dx = u - corners[i].x;
                float dy = v - corners[i].y;
                sum += dx * dx + dy * dy;
            }
            return Mathf.Sqrt(sum / 4f);
        }

        // Rodrigues: a rotation vector to the matrix it stands for
        private static Matrix4x4 ExpMap(Vector3 omega)
        {
            float angle = omega.magnitude;
            if (angle < 1e-9f)
                return Matrix4x4.identity;

            Vector3 axis = omega / angle;
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            float t = 1f - cos;

            var m = Matrix4x4.identity;
            m.m00 = cos + axis.x * axis.x * t;
            m.m01 = axis.x * axis.y * t - axis.z * sin;
            m.m02 = axis.x * axis.z * t + axis.y * sin;
            m.m10 = axis.y * axis.x * t + axis.z * sin;
            m.m11 = cos + axis.y * axis.y * t;
            m.m12 = axis.y * axis.z * t - axis.x * sin;
            m.m20 = axis.z * axis.x * t - axis.y * sin;
            m.m21 = axis.z * axis.y * t + axis.x * sin;
            m.m22 = cos + axis.z * axis.z * t;
            return m;
        }

        private static void Orthonormalise(ref Vector3 r1, ref Vector3 r2, out Vector3 r3)
        {
            r1.Normalize();
            r2 -= Vector3.Dot(r1, r2) * r1;
            r2.Normalize();
            r3 = Vector3.Cross(r1, r2);
        }

        private static Matrix4x4 FromColumns(Vector3 c0, Vector3 c1, Vector3 c2)
        {
            var m = Matrix4x4.identity;
            m.m00 = c0.x; m.m10 = c0.y; m.m20 = c0.z;
            m.m01 = c1.x; m.m11 = c1.y; m.m21 = c1.z;
            m.m02 = c2.x; m.m12 = c2.y; m.m22 = c2.z;
            return m;
        }

        private static Vector3 Column(Matrix4x4 m, int index)
        {
            var column = m.GetColumn(index);
            return new Vector3(column.x, column.y, column.z);
        }

        // OpenCV's camera frame is x right, y DOWN, z forward; Unity's is x right, y UP, z
        // forward. The two differ by a single Y flip - the same "camera optical frame is not
        // FLU" exception the point cloud pipeline lives by (see CLAUDE.md).
        //
        // The flip is a REFLECTION, and that has a consequence worth stating plainly, because it
        // is the easiest thing in this file to get quietly wrong: applying it to all three of the
        // marker's axis vectors gives a triple with determinant -1, which is not a rotation and
        // cannot be a Quaternion. Something has to give. What is kept here is the pair of axes
        // the calibration actually uses:
        //
        //     Rotation * Vector3.forward  =  out of the marker's printed face
        //     Rotation * Vector3.up       =  towards the top of the printed image
        //
        // and what is given up is the third: Rotation * Vector3.right points to the printed
        // LEFT, not the printed right. Nothing downstream reads the X axis, but anything added
        // later that does must know it is mirrored.
        //
        // Sanity check if this ever looks wrong: a tag held square-on to the camera must come out
        // rotated 180 degrees about Y from the camera, because its printed face looks back at
        // the lens.
        private static void ToUnity(Matrix4x4 rotation, Vector3 translation, out Vector3 position,
            out Quaternion unityRotation)
        {
            position = new Vector3(translation.x, -translation.y, translation.z);

            Vector3 up = FlipY(Column(rotation, 1));
            Vector3 forward = FlipY(Column(rotation, 2));

            unityRotation = FromUpForward(up, forward);
        }

        private static Vector3 FlipY(Vector3 v)
        {
            return new Vector3(v.x, -v.y, v.z);
        }

        // Quaternion.LookRotation's job, done in managed code so the whole solver can be run and
        // checked outside the engine - LookRotation is an engine extern and throws the moment it
        // is called from a plain test host.
        private static Quaternion FromUpForward(Vector3 up, Vector3 forward)
        {
            forward.Normalize();
            up -= Vector3.Dot(up, forward) * forward;

            if (up.sqrMagnitude < 1e-12f)
            {
                // Degenerate input: pick any axis perpendicular to forward rather than emit NaN
                up = Mathf.Abs(forward.y) < 0.9f ? Vector3.up : Vector3.right;
                up -= Vector3.Dot(up, forward) * forward;
            }

            up.Normalize();
            Vector3 right = Vector3.Cross(up, forward);

            // Standard orthonormal-basis-to-quaternion, taking the largest-magnitude branch so
            // the square root never operates on something near zero
            float m00 = right.x, m10 = right.y, m20 = right.z;
            float m01 = up.x, m11 = up.y, m21 = up.z;
            float m02 = forward.x, m12 = forward.y, m22 = forward.z;

            float trace = m00 + m11 + m22;
            Quaternion q;

            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }

            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (magnitude < 1e-9f)
                return Quaternion.identity;

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }
    }
}
