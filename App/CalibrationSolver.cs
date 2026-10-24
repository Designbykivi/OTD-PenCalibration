using System;
using System.Collections.Generic;
using System.Linq;

namespace PenCalibration.App
{
    public enum FitMode
    {
        /// <summary>Offset, scale, rotation and skew. Needs 3+ targets.</summary>
        Affine,
        /// <summary>Affine plus keystone (tilted panel / parallax gradient). Needs 4+ targets.</summary>
        Perspective
    }

    /// <param name="Target">Where the crosshair was drawn, in window coordinates.</param>
    /// <param name="Click">Where the OS reported the pen tap, in window coordinates.</param>
    /// <param name="Raw">Uncorrected position the filter saw for that tap, in OTD output coordinates.</param>
    public record Sample((double X, double Y) Target, (double X, double Y) Click, (double X, double Y) Raw);

    /// <param name="Correction">Transform for the filter, in OTD output coordinates.</param>
    /// <param name="ErrorBefore">Per-target distance between tap and crosshair, in window units, before calibration.</param>
    /// <param name="ErrorAfter">The same distances with the correction applied.</param>
    /// <param name="SyncError">How well the app's and the driver's coordinates agree. Large values mean the
    /// measurement is unreliable (another program moved the cursor, relative mode, etc.).</param>
    /// <param name="MaxShift">Largest movement the correction causes anywhere on the window, as a fraction of its diagonal.</param>
    /// <param name="CentreShift">How far the correction moves the cursor at the window's centre, in OTD output coordinates.</param>
    public record CalibrationResult(Transform2D Correction, double[] ErrorBefore, double[] ErrorAfter, double SyncError, double MaxShift,
        (double X, double Y) CentreShift)
    {
        public const double MaxSyncError = 4;
        public const double MaxReasonableShift = 0.2;
        /// <summary>A target this far (window units) from where the fit puts it doesn't agree with the others.</summary>
        public const double MaxResidual = 12;

        private int WorstTarget => Array.IndexOf(ErrorAfter, ErrorAfter.Max());

        public string? Problem =>
            SyncError > MaxSyncError
                ? $"The driver's pen position and the cursor disagree by {SyncError:F1} px. Make sure OTD is in Absolute Mode, " +
                  "Pen Calibration is the last filter, and nothing else moves the cursor."
            : MaxShift > MaxReasonableShift
                ? $"The correction would move the cursor by {MaxShift:P0} of the screen. A target was probably missed — try again."
            : ErrorAfter.Max() > MaxResidual
                ? $"Target {WorstTarget + 1} doesn't line up with the others ({ErrorAfter.Max():F0} px off after correction). " +
                  "It was probably missed — try again."
            : null;
    }

    public static class CalibrationSolver
    {
        public static int MinimumPoints(FitMode mode) => mode == FitMode.Perspective ? 4 : 3;

        public static CalibrationResult Solve(IReadOnlyList<Sample> samples, FitMode mode, double windowWidth, double windowHeight)
        {
            if (samples.Count < MinimumPoints(mode))
                throw new ArgumentException($"{mode} calibration needs at least {MinimumPoints(mode)} targets.");

            // OTD's output space and the window's coordinates differ by DPI scaling and the window's
            // position on the desktop. Each tap gives the same physical point in both systems
            // (correction is bypassed while measuring), so the mapping can be measured directly
            // instead of guessing how each OS reports display bounds.
            var clicks = samples.Select(s => s.Click).ToList();
            var raws = samples.Select(s => s.Raw).ToList();
            var windowToOtd = Transform2D.FitScaleTranslate(clicks, raws);
            var otdToWindow = windowToOtd.Inverse();

            double syncError = samples.Max(s => Distance(otdToWindow.Apply(s.Raw.X, s.Raw.Y), s.Click));

            var targetsOtd = samples.Select(s => windowToOtd.Apply(s.Target.X, s.Target.Y)).ToList();
            var correction = mode == FitMode.Perspective
                ? Transform2D.FitPerspective(raws, targetsOtd)
                : Transform2D.FitAffine(raws, targetsOtd);

            // The same correction expressed in window coordinates, for reporting.
            var inWindow = windowToOtd.Then(correction).Then(otdToWindow);

            var before = samples.Select(s => Distance(s.Click, s.Target)).ToArray();
            var after = samples.Select(s => Distance(inWindow.Apply(s.Click.X, s.Click.Y), s.Target)).ToArray();

            double diagonal = Math.Sqrt(windowWidth * windowWidth + windowHeight * windowHeight);
            double maxShift = 0;
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                {
                    var p = (windowWidth * i / 4, windowHeight * j / 4);
                    maxShift = Math.Max(maxShift, Distance(inWindow.Apply(p.Item1, p.Item2), p) / diagonal);
                }

            var centre = windowToOtd.Apply(windowWidth / 2, windowHeight / 2);
            var moved = correction.Apply(centre.X, centre.Y);
            var centreShift = (moved.X - centre.X, moved.Y - centre.Y);

            return new CalibrationResult(correction, before, after, syncError, maxShift, centreShift);
        }

        /// <summary>Target counts offered by the app, in the order T cycles through them.</summary>
        public static IReadOnlyList<int> TargetCounts { get; } = new[] { 4, 5, 9, 16, 25 };

        /// <summary>Target positions as fractions of the window, Wacom-style: inset from the corners.</summary>
        public static IReadOnlyList<(double X, double Y)> TargetLayout(int count)
        {
            const double lo = 0.04, mid = 0.5, hi = 0.96;
            return count switch
            {
                4 => new[] { (lo, lo), (hi, lo), (hi, hi), (lo, hi) },
                5 => new[] { (lo, lo), (hi, lo), (hi, hi), (lo, hi), (mid, mid) },
                9 => new[] { (lo, lo), (mid, lo), (hi, lo), (hi, mid), (hi, hi), (mid, hi), (lo, hi), (lo, mid), (mid, mid) },
                16 => Grid(4, lo, hi),
                25 => Grid(5, lo, hi),
                _ => throw new ArgumentOutOfRangeException(nameof(count), "Use 4, 5, 9, 16 or 25 targets.")
            };
        }

        /// <summary>An n×n grid, visited row by row in a zig-zag so the pen never jumps across the screen.</summary>
        private static (double X, double Y)[] Grid(int n, double lo, double hi)
        {
            double Step(int i) => lo + (hi - lo) * i / (n - 1);
            return Enumerable.Range(0, n)
                .SelectMany(row => Enumerable.Range(0, n).Select(col => (Step(row % 2 == 0 ? col : n - 1 - col), Step(row))))
                .ToArray();
        }

        private static double Distance((double X, double Y) a, (double X, double Y) b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }
}
