using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using OpenTabletDriver.Plugin;

namespace PenCalibration
{
    /// <summary>
    /// Starts the calibration app from the daemon. OTD's settings UI has no buttons for plugins,
    /// so the filter offers a "Calibrate" checkbox instead.
    /// </summary>
    public static class AppLauncher
    {
        public const string APP_NAME = "PenCalibrationApp";

        // The daemon re-creates filters when it starts, not just on Apply. Don't pop the app up at login.
        private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(10);

        /// <summary>True while the daemon is starting up, when filters load from saved settings rather than an Apply.</summary>
        public static bool DaemonStartingUp => DateTime.Now - Process.GetCurrentProcess().StartTime < StartupGrace;

        // One Apply loads the filter once per tablet; launch only once for all of them.
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

        private static readonly object Sync = new();
        private static DateTime lastLaunch = DateTime.MinValue;

        public static void LaunchOnApply()
        {
            try
            {
                lock (Sync)
                {
                    var now = DateTime.Now;
                    if (DaemonStartingUp || now - lastLaunch < Debounce)
                        return;
                    lastLaunch = now;
                }

                if (Process.GetProcessesByName(APP_NAME).Length > 0)
                {
                    Log.Write(PenCalibrationFilter.PLUGIN_NAME, "The calibration app is already running.");
                    return;
                }

                var appDir = Path.Combine(FindPluginDirectory() ?? "", "App", PlatformFolder);
                var path = FindApp(appDir);
                if (path == null)
                {
                    Log.Write(PenCalibrationFilter.PLUGIN_NAME,
                        $"No calibration app for this platform in '{appDir}'. Reinstall the plugin with its App folder.",
                        LogLevel.Error);
                    return;
                }

                EnsureExecutable(path);

                // A macOS .app bundle has to go through LaunchServices; anything else is started directly.
                var start = path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)
                    ? new ProcessStartInfo("open", $"-n \"{path}\"")
                    : new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
                start.UseShellExecute = false;

                Process.Start(start)?.Dispose();
                Log.Write(PenCalibrationFilter.PLUGIN_NAME, "Started the calibration app. Untick \"Calibrate\" when you're done.");
            }
            catch (Exception ex)
            {
                Log.Write(PenCalibrationFilter.PLUGIN_NAME, $"Could not start the calibration app: {ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>The plugin's folder; the app is bundled in its App subfolder.</summary>
        private static string? FindPluginDirectory()
        {
            // OTD loads plugins from a stream, so Assembly.Location is empty. Its load context
            // (DesktopPluginContext) knows the folder, but that type isn't in the plugin API.
            var assembly = typeof(AppLauncher).Assembly;
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            if (context?.GetType().GetProperty("Directory")?.GetValue(context) is DirectoryInfo directory)
                return directory.FullName;

            return string.IsNullOrEmpty(assembly.Location) ? null : Path.GetDirectoryName(assembly.Location);
        }

        /// <summary>The release ships one app per platform, in App/&lt;runtime identifier&gt;.</summary>
        private static string PlatformFolder
        {
            get
            {
                bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
                if (OperatingSystem.IsMacOS())
                    return arm ? "osx-arm64" : "osx-x64";
                if (OperatingSystem.IsWindows())
                    return "win-x64"; // Windows on Arm runs x64 apps under emulation.
                return arm ? "linux-arm64" : "linux-x64";
            }
        }

        // Zip extraction doesn't always keep the executable bit.
        private static void EnsureExecutable(string path)
        {
            if (OperatingSystem.IsWindows())
                return;

            var binary = Directory.Exists(path)
                ? Path.Combine(path, "Contents", "MacOS", APP_NAME)
                : path;
            if (File.Exists(binary))
                File.SetUnixFileMode(binary, File.GetUnixFileMode(binary) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        private static string? FindApp(string appDir)
        {
            foreach (var name in new[] { APP_NAME + ".app", APP_NAME + ".exe", APP_NAME })
            {
                var candidate = Path.Combine(appDir, name);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
