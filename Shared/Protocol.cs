namespace PenCalibration
{
    /// <summary>
    /// Line-based text protocol between the calibration app and the OTD filter, over TCP on 127.0.0.1.
    /// Every request gets exactly one reply line starting with "OK" or "ERR".
    ///
    ///   HELLO                 -> OK &lt;version&gt;
    ///   BEGIN                 -> OK          (filter passes raw positions through until END or disconnect)
    ///   POS                   -> OK &lt;x&gt; &lt;y&gt; &lt;ageMs&gt; &lt;tablet name&gt;   (latest uncorrected output position)
    ///   SCREEN                -> OK &lt;index&gt; &lt;count&gt; &lt;ageMs&gt;   (monitor under the latest pen position, counted
    ///                            left to right then top to bottom, and how many monitors OTD sees)
    ///   APPLY &lt;CalibrationRecord JSON&gt;  -> OK   (saves and activates a calibration)
    ///   CLEAR &lt;tablet name&gt;   -> OK          (removes the calibration)
    ///   END                   -> OK
    /// </summary>
    public static class Protocol
    {
        public const int DefaultPort = 47853;

        /// <summary>OTD_PENCALIBRATION_PORT overrides the port; the tests use it to stay away from a running OTD.</summary>
        public static int Port { get; } =
            int.TryParse(System.Environment.GetEnvironmentVariable("OTD_PENCALIBRATION_PORT"), out var port) ? port : DefaultPort;
        public const string Version = "2";
    }
}
