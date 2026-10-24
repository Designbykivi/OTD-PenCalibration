using System;
using System.Collections.Generic;

namespace PenCalibration
{
    /// <summary>
    /// A saved calibration: the correction matrix plus how it was measured, for showing in OTD's
    /// Filters tab. Sent by the app as JSON with APPLY and stored per tablet in calibrations.json.
    /// </summary>
    public sealed class CalibrationRecord
    {
        public string Tablet { set; get; } = "";
        public double[] Matrix { set; get; } = Array.Empty<double>();
        public DateTime? Saved { set; get; }
        public int? Targets { set; get; }
        public string? Mode { set; get; }
        public double? AverageErrorBefore { set; get; }
        public double? WorstErrorBefore { set; get; }
        public double? AverageErrorAfter { set; get; }
        public double? WorstErrorAfter { set; get; }

        /// <summary>How far the correction moves the cursor at the centre of the calibrated screen, in OTD pixels.</summary>
        public double[]? CentreShift { set; get; }

        /// <summary>Lines for the "Calibration" dropdown in OTD's Filters tab.</summary>
        /// <param name="withTabletName">Name the tablet, for when more than one is calibrated.</param>
        public IEnumerable<string> Describe(bool withTabletName)
        {
            string who = withTabletName ? $"{Tablet}: " : "";
            yield return Saved is DateTime saved
                ? $"✓ {who}Calibrated {saved:d MMM yyyy, HH:mm}"
                : $"✓ {who}Calibrated";

            if (AverageErrorBefore is double avgBefore && AverageErrorAfter is double avgAfter
                && WorstErrorBefore is double worstBefore && WorstErrorAfter is double worstAfter)
                yield return $"Accuracy: {avgBefore:0.0} → {avgAfter:0.0} px average, {worstBefore:0.0} → {worstAfter:0.0} px worst";
            if (CentreShift is { Length: 2 } shift)
                yield return $"Offset at centre: {Signed(shift[0])}, {Signed(shift[1])} px";
            if (Matrix.Length == 9)
            {
                // Scale and rotation of the linear part; keystone terms are tiny and not worth a line.
                double sx = Math.Sqrt(Matrix[0] * Matrix[0] + Matrix[3] * Matrix[3]);
                double sy = Math.Sqrt(Matrix[1] * Matrix[1] + Matrix[4] * Matrix[4]);
                double rotation = Math.Atan2(Matrix[3], Matrix[0]) * 180 / Math.PI;
                yield return $"Scale: {sx * 100:0.0}% × {sy * 100:0.0}%  ·  Rotation: {Signed(rotation, "0.00")}°";
            }
            yield return Targets is int targets
                ? $"Measured with {targets} targets  ·  {Mode ?? "Perspective"}"
                : "Recalibrate to see accuracy details.";
        }

        // Typographic minus, so positive and negative values line up.
        private static string Signed(double value, string format = "0.0")
            => value.ToString("+" + format + ";−" + format + ";" + format);
    }
}
