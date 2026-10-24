using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenTabletDriver.Plugin;

namespace PenCalibration
{
    /// <summary>
    /// Saves calibrations as JSON, keyed by tablet name. Kept outside the plugin folder
    /// so reinstalling or updating the plugin doesn't wipe it.
    /// </summary>
    public static class CalibrationStore
    {
        private static readonly object Sync = new();

        public static string FilePath { get; } = Environment.GetEnvironmentVariable("OTD_PENCALIBRATION_FILE")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenTabletDriver-PenCalibration",
                "calibrations.json");

        public static Transform2D? Load(string tablet)
        {
            lock (Sync)
            {
                return ReadAll().TryGetValue(tablet, out var record) ? TryCreate(record.Matrix) : null;
            }
        }

        public static IReadOnlyList<CalibrationRecord> LoadAll()
        {
            lock (Sync)
            {
                return ReadAll().Values.ToList();
            }
        }

        /// <param name="record">The calibration to save, or null to remove the tablet's calibration.</param>
        public static void Save(string tablet, CalibrationRecord? record)
        {
            lock (Sync)
            {
                var all = ReadAll();
                if (record == null)
                    all.Remove(tablet);
                else
                    all[tablet] = record;

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(all, CalibrationJson.Default.DictionaryStringCalibrationRecord));
                File.Move(temp, FilePath, overwrite: true);
            }
        }

        private static Dictionary<string, CalibrationRecord> ReadAll()
        {
            var all = new Dictionary<string, CalibrationRecord>();
            try
            {
                if (!File.Exists(FilePath))
                    return all;

                var raw = JsonSerializer.Deserialize(File.ReadAllText(FilePath), CalibrationJson.Default.DictionaryStringJsonElement) ?? new();
                foreach (var (tablet, value) in raw)
                {
                    // Version 1 files stored just the matrix as an array.
                    var record = value.ValueKind == JsonValueKind.Array
                        ? new CalibrationRecord { Matrix = value.Deserialize(CalibrationJson.Default.DoubleArray) ?? Array.Empty<double>() }
                        : value.Deserialize(CalibrationJson.Default.CalibrationRecord);
                    if (record != null)
                    {
                        record.Tablet = tablet;
                        all[tablet] = record;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write(PenCalibrationFilter.PLUGIN_NAME, $"Could not read {FilePath}: {ex.Message}", LogLevel.Warning);
            }
            return all;
        }

        private static Transform2D? TryCreate(double[] values)
        {
            try
            {
                return new Transform2D(values);
            }
            catch (ArgumentException ex)
            {
                Log.Write(PenCalibrationFilter.PLUGIN_NAME, $"Ignoring invalid saved calibration: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }
    }
}
