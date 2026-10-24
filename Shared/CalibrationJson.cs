using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenCalibration
{
    /// <summary>
    /// Compile-time JSON serialization. The calibration app is published trimmed, where
    /// reflection-based System.Text.Json is switched off.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(CalibrationRecord))]
    [JsonSerializable(typeof(Dictionary<string, CalibrationRecord>))]
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(double[]))]
    public partial class CalibrationJson : JsonSerializerContext
    {
    }
}
