using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace PenCalibration
{
    /// <summary>
    /// "Pen Calibration": corrects the pen's screen position with a transform measured by the
    /// Pen Calibration app, the same way Wacom's "Calibrate" tab does for pen displays.
    /// Runs after OTD maps the tablet area to the display, so it works in screen coordinates.
    /// </summary>
    [PluginName(PLUGIN_NAME)]
    public class PenCalibrationFilter : IPositionedPipelineElement<IDeviceReport>
    {
        public const string PLUGIN_NAME = "Pen Calibration";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        internal static long ElapsedTicks => Clock.ElapsedTicks;

        private volatile Transform2D? calibration;
        private string? tabletName;

        [TabletReference]
        public TabletReference? Tablet { set; get; }

        public const string DETAILS_HEADER = "Calibration details";

        // OTD's settings UI has no plain-text labels and only shows tooltips on hover, so the guidance
        // lives in the section headings and the checkbox label. The dropdown is filled by calling a
        // static member of the plugin when the filter is opened, which is used to list the saved calibration.
        [Property("Calibration  —  open the list for details. Reopen this filter to refresh it after calibrating."),
         PropertyValidated(nameof(CalibrationDetails)), DefaultPropertyValue(DETAILS_HEADER)]
        public string Details { set; get; } = DETAILS_HEADER;

        public static IEnumerable<string> CalibrationDetails
        {
            get
            {
                var lines = new List<string> { DETAILS_HEADER };
                try
                {
                    var records = CalibrationStore.LoadAll();
                    lines.AddRange(records.SelectMany(r => r.Describe(withTabletName: records.Count > 1)));
                    if (records.Count == 0)
                        lines.Add("✗ Not calibrated yet. Tick \"Calibrate\" below and click Apply.");
                }
                catch (Exception ex)
                {
                    lines.Add("Could not read calibrations: " + ex.Message);
                }
                return lines;
            }
        }

        [BooleanProperty("Calibrate  —  tick this, then click Apply. Untick it when you're done, or it opens on every Apply.",
                         "Open the calibration screen on Apply"), DefaultPropertyValue(false)]
        public bool RunOnApply { set; get; }

        public PipelinePosition Position => PipelinePosition.PostTransform;

        public event Action<IDeviceReport>? Emit;

        /// <summary>Name the calibration is stored under. One calibration per tablet model.</summary>
        public string TabletName => tabletName ??= Tablet?.Properties?.Name ?? "Unknown tablet";

        /// <summary>Latest uncorrected position, read by the calibration app. Written on the report thread.</summary>
        public RawSample? LastSample => lastSample;

        private volatile RawSample? lastSample;

        [OnDependencyLoad]
        public void OnLoad()
        {
            calibration = CalibrationStore.Load(TabletName);
            Log.Write(PLUGIN_NAME, calibration == null
                ? $"No calibration saved for '{TabletName}'. Tick \"Calibrate\" in the filter and click Apply to create one."
                : $"Loaded calibration for '{TabletName}'.");

            CalibrationServer.Register(this);

            if (RunOnApply)
                AppLauncher.LaunchOnApply();
        }

        public void SetCalibration(Transform2D? value) => calibration = value;

        public void Consume(IDeviceReport value)
        {
            // Filters run on the tablet's report thread: an exception escaping here kills the
            // reader and OTD loses the tablet until "Detect tablet". Never let one through.
            try
            {
                if (value is IAbsolutePositionReport report)
                {
                    lastSample = new RawSample(report.Position, Clock.ElapsedTicks);
                    CalibrationServer.MarkActive(this);

                    var current = calibration;
                    if (current != null && !CalibrationServer.Bypass)
                        report.Position = current.Apply(report.Position);
                }
            }
            catch (Exception ex)
            {
                Log.Write(PLUGIN_NAME, $"Correction failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }

            Emit?.Invoke(value);
        }
    }

    public sealed record RawSample(Vector2 Position, long Ticks)
    {
        public TimeSpan Age => TimeSpan.FromSeconds((PenCalibrationFilter.ElapsedTicks - Ticks) / (double)Stopwatch.Frequency);
    }
}
