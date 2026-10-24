using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenTabletDriver.Plugin;

namespace PenCalibration
{
    /// <summary>
    /// Localhost endpoint the calibration app talks to (see <see cref="Protocol"/>).
    /// One server per OTD daemon, shared by every filter instance.
    /// </summary>
    public static class CalibrationServer
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, PenCalibrationFilter> Filters = new();
        private static volatile PenCalibrationFilter? active;
        private static int bypassClients;
        private static bool started;

        /// <summary>True while a calibration session is measuring, so the app sees uncorrected positions.</summary>
        public static bool Bypass => Volatile.Read(ref bypassClients) > 0;

        public static void Register(PenCalibrationFilter filter)
        {
            lock (Sync)
            {
                // OTD builds new filter instances whenever settings are applied; the newest one wins.
                Filters[filter.TabletName] = filter;
                if (!started)
                {
                    started = true;
                    Task.Run(ListenAsync);
                }
            }
        }

        public static void MarkActive(PenCalibrationFilter filter)
        {
            if (!ReferenceEquals(active, filter))
                active = filter;
        }

        private static async Task ListenAsync()
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Protocol.Port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.Write(PenCalibrationFilter.PLUGIN_NAME,
                    $"Could not listen on 127.0.0.1:{Protocol.Port} ({ex.Message}). The calibration app won't be able to connect.",
                    LogLevel.Error);
                return;
            }

            while (true)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (Exception ex)
                {
                    Log.Debug(PenCalibrationFilter.PLUGIN_NAME, "Accept failed: " + ex.Message);
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            bool bypassing = false;
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        string reply;
                        try
                        {
                            reply = Handle(line.Trim(), ref bypassing);
                        }
                        catch (Exception ex)
                        {
                            reply = "ERR " + ex.Message;
                        }
                        await writer.WriteLineAsync(reply);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(PenCalibrationFilter.PLUGIN_NAME, "Calibration client error: " + ex.Message);
            }
            finally
            {
                // Never leave the correction switched off because the app crashed mid-calibration.
                if (bypassing)
                    Interlocked.Decrement(ref bypassClients);
            }
        }

        private static string Handle(string line, ref bool bypassing)
        {
            var parts = line.Split(' ', 2);
            string command = parts[0].ToUpperInvariant();
            string args = parts.Length > 1 ? parts[1] : "";

            switch (command)
            {
                case "HELLO":
                    return "OK " + Protocol.Version;

                case "BEGIN":
                    if (!bypassing)
                    {
                        bypassing = true;
                        Interlocked.Increment(ref bypassClients);
                    }
                    return "OK";

                case "END":
                    if (bypassing)
                    {
                        bypassing = false;
                        Interlocked.Decrement(ref bypassClients);
                    }
                    return "OK";

                case "POS":
                {
                    var filter = active;
                    var sample = filter?.LastSample;
                    if (filter == null || sample == null)
                        return "ERR No pen reports yet. Is the Pen Calibration filter enabled, and is the pen in range?";

                    var c = CultureInfo.InvariantCulture;
                    return string.Format(c, "OK {0:R} {1:R} {2:F0} {3}",
                        sample.Position.X, sample.Position.Y, sample.Age.TotalMilliseconds, filter.TabletName);
                }

                case "SCREEN":
                {
                    var sample = active?.LastSample;
                    if (sample == null)
                        return "ERR No pen reports yet.";
                    if (DisplayLocator.Locate(sample.Position) is not var (index, count))
                        return "ERR Display list unavailable.";
                    return string.Format(CultureInfo.InvariantCulture, "OK {0} {1} {2:F0}", index, count, sample.Age.TotalMilliseconds);
                }

                case "APPLY":
                {
                    var record = JsonSerializer.Deserialize(args, CalibrationJson.Default.CalibrationRecord)
                        ?? throw new FormatException("APPLY needs a calibration.");
                    record.Saved = DateTime.Now;
                    return SetFor(record.Tablet, record);
                }

                case "CLEAR":
                    return SetFor(args, null);

                default:
                    return "ERR Unknown command: " + command;
            }
        }

        /// <summary>Removes a tablet's calibration, as the Reset checkbox in the filter does.</summary>
        public static void Clear(string tablet) => SetFor(tablet, null);

        private static string SetFor(string tablet, CalibrationRecord? record)
        {
            if (string.IsNullOrWhiteSpace(tablet))
                return "ERR Missing tablet name.";

            // Validates the matrix before anything is saved.
            var transform = record == null ? null : new Transform2D(record.Matrix);
            CalibrationStore.Save(tablet, record);
            lock (Sync)
            {
                if (Filters.TryGetValue(tablet, out var filter))
                    filter.SetCalibration(transform);
            }

            Log.Write(PenCalibrationFilter.PLUGIN_NAME, transform == null
                ? $"Calibration cleared for '{tablet}'."
                : $"Calibration saved for '{tablet}': {transform}");
            return "OK";
        }
    }
}
