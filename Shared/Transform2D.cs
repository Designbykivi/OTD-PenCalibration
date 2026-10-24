using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace PenCalibration
{
    /// <summary>
    /// A 2D projective transform stored as a row-major 3x3 matrix.
    /// Affine transforms are the special case where the bottom row is (0, 0, 1).
    /// </summary>
    public sealed class Transform2D
    {
        public static Transform2D Identity { get; } = new Transform2D(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });

        private readonly double[] m;

        public Transform2D(double[] values)
        {
            if (values.Length != 9)
                throw new ArgumentException("A 2D transform needs 9 values.", nameof(values));
            if (values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("Transform contains NaN or infinity.", nameof(values));

            // Normalise so m22 == 1 whenever possible; makes stored values easy to read.
            double s = Math.Abs(values[8]) > 1e-12 ? values[8] : 1;
            m = values.Select(v => v / s).ToArray();
        }

        public double[] Values => (double[])m.Clone();

        public (double X, double Y) Apply(double x, double y)
        {
            double w = m[6] * x + m[7] * y + m[8];
            if (Math.Abs(w) < 1e-12)
                return (x, y);
            return ((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
        }

        public Vector2 Apply(Vector2 p)
        {
            var (x, y) = Apply(p.X, p.Y);
            return new Vector2((float)x, (float)y);
        }

        /// <summary>Composition that applies this transform first, then <paramref name="next"/>.</summary>
        public Transform2D Then(Transform2D next) => Multiply(next.m, m);

        public Transform2D Inverse()
        {
            var a = m;
            double det = a[0] * (a[4] * a[8] - a[5] * a[7])
                       - a[1] * (a[3] * a[8] - a[5] * a[6])
                       + a[2] * (a[3] * a[7] - a[4] * a[6]);
            if (Math.Abs(det) < 1e-15)
                throw new InvalidOperationException("Transform is not invertible.");

            return new Transform2D(new[]
            {
                (a[4] * a[8] - a[5] * a[7]) / det, (a[2] * a[7] - a[1] * a[8]) / det, (a[1] * a[5] - a[2] * a[4]) / det,
                (a[5] * a[6] - a[3] * a[8]) / det, (a[0] * a[8] - a[2] * a[6]) / det, (a[2] * a[3] - a[0] * a[5]) / det,
                (a[3] * a[7] - a[4] * a[6]) / det, (a[1] * a[6] - a[0] * a[7]) / det, (a[0] * a[4] - a[1] * a[3]) / det,
            });
        }

        public static Transform2D ScaleTranslate(double sx, double sy, double tx, double ty)
            => new Transform2D(new[] { sx, 0, tx, 0, sy, ty, 0, 0, 1 });

        public string Serialize() => string.Join(" ", m.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

        public static Transform2D Parse(IEnumerable<string> parts)
            => new Transform2D(parts.Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray());

        public override string ToString() => Serialize();

        private static Transform2D Multiply(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return new Transform2D(r);
        }

        // ---- Fitting -------------------------------------------------------------------------

        /// <summary>
        /// Least-squares fit of independent X/Y scale and offset (no rotation or skew).
        /// Used to relate the calibration window's coordinates to OTD's output coordinates.
        /// </summary>
        public static Transform2D FitScaleTranslate(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
        {
            CheckPairs(from, to, 2);
            var (sx, tx) = FitLine(from.Select(p => p.X).ToArray(), to.Select(p => p.X).ToArray());
            var (sy, ty) = FitLine(from.Select(p => p.Y).ToArray(), to.Select(p => p.Y).ToArray());
            return ScaleTranslate(sx, sy, tx, ty);
        }

        /// <summary>Least-squares affine fit (offset, scale, rotation, skew). Needs 3+ non-collinear points.</summary>
        public static Transform2D FitAffine(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
        {
            CheckPairs(from, to, 3);
            var (tFrom, nFrom) = Normalize(from);
            var (tTo, nTo) = Normalize(to);

            var ata = new double[3, 3];
            var atbx = new double[3];
            var atby = new double[3];
            for (int i = 0; i < nFrom.Length; i++)
            {
                double[] row = { nFrom[i].X, nFrom[i].Y, 1 };
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                        ata[r, c] += row[r] * row[c];
                    atbx[r] += row[r] * nTo[i].X;
                    atby[r] += row[r] * nTo[i].Y;
                }
            }

            var x = Solve((double[,])ata.Clone(), atbx);
            var y = Solve(ata, atby);
            var normalized = new Transform2D(new[] { x[0], x[1], x[2], y[0], y[1], y[2], 0, 0, 1 });
            return Denormalize(normalized, tFrom, tTo);
        }

        /// <summary>
        /// Least-squares perspective (homography) fit via normalised DLT with h22 = 1.
        /// Needs 4+ points, no three collinear. Exact with 4 points, least squares with more.
        /// </summary>
        public static Transform2D FitPerspective(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
        {
            CheckPairs(from, to, 4);
            var (tFrom, nFrom) = Normalize(from);
            var (tTo, nTo) = Normalize(to);

            var ata = new double[8, 8];
            var atb = new double[8];
            for (int i = 0; i < nFrom.Length; i++)
            {
                var (x, y) = nFrom[i];
                var (u, v) = nTo[i];
                double[] r1 = { x, y, 1, 0, 0, 0, -u * x, -u * y };
                double[] r2 = { 0, 0, 0, x, y, 1, -v * x, -v * y };
                Accumulate(ata, atb, r1, u);
                Accumulate(ata, atb, r2, v);
            }

            var h = Solve(ata, atb);
            var normalized = new Transform2D(new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1 });
            return Denormalize(normalized, tFrom, tTo);
        }

        private static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
        {
            int n = row.Length;
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                    ata[r, c] += row[r] * row[c];
                atb[r] += row[r] * rhs;
            }
        }

        private static void CheckPairs(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to, int min)
        {
            if (from.Count != to.Count)
                throw new ArgumentException("Point lists must have the same length.");
            if (from.Count < min)
                throw new ArgumentException($"At least {min} points are needed.");
        }

        private static (double Scale, double Offset) FitLine(double[] x, double[] y)
        {
            double mx = x.Average(), my = y.Average();
            double sxx = x.Sum(v => (v - mx) * (v - mx));
            double sxy = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum();
            if (sxx < 1e-9)
                throw new InvalidOperationException("Points are too close together to fit a scale.");
            double scale = sxy / sxx;
            return (scale, my - scale * mx);
        }

        // Hartley normalisation: centre on the centroid and scale to a mean distance of sqrt(2).
        // Keeps the normal equations well conditioned when coordinates are in the thousands.
        private static (Transform2D T, (double X, double Y)[] Points) Normalize(IReadOnlyList<(double X, double Y)> pts)
        {
            double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
            double mean = pts.Average(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
            if (mean < 1e-9)
                throw new InvalidOperationException("All points are in the same place.");
            double s = Math.Sqrt(2) / mean;
            var t = ScaleTranslate(s, s, -s * cx, -s * cy);
            return (t, pts.Select(p => t.Apply(p.X, p.Y)).ToArray());
        }

        private static Transform2D Denormalize(Transform2D normalized, Transform2D tFrom, Transform2D tTo)
            => tFrom.Then(normalized).Then(tTo.Inverse());

        /// <summary>Gaussian elimination with partial pivoting.</summary>
        private static double[] Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            b = (double[])b.Clone();
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                        pivot = r;
                if (Math.Abs(a[pivot, col]) < 1e-12)
                    throw new InvalidOperationException("Points are degenerate (too few, or lined up).");

                if (pivot != col)
                {
                    for (int c = 0; c < n; c++)
                        (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    (b[col], b[pivot]) = (b[pivot], b[col]);
                }

                for (int r = col + 1; r < n; r++)
                {
                    double f = a[r, col] / a[col, col];
                    for (int c = col; c < n; c++)
                        a[r, c] -= f * a[col, c];
                    b[r] -= f * b[col];
                }
            }

            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = b[r];
                for (int c = r + 1; c < n; c++)
                    sum -= a[r, c] * x[c];
                x[r] = sum / a[r, r];
            }
            return x;
        }
    }
}
