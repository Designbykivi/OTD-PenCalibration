using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PenCalibration.App
{
    public record RawPosition(double X, double Y, double AgeMs, string Tablet);

    /// <summary>Talks to the Pen Calibration filter inside the OTD daemon. See <see cref="Protocol"/>.</summary>
    public sealed class DriverClient : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private TcpClient? tcp;
        private StreamReader? reader;
        private StreamWriter? writer;

        public bool Connected => tcp?.Connected == true;

        public async Task ConnectAsync()
        {
            Dispose();
            tcp = new TcpClient { NoDelay = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync("127.0.0.1", Protocol.Port, timeout.Token);
            var stream = tcp.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            var version = await SendAsync("HELLO");
            if (version != Protocol.Version)
                throw new IOException($"The installed filter speaks protocol {version}, this app speaks {Protocol.Version}. Update both to the same release.");
        }

        public Task BeginAsync() => SendAsync("BEGIN");

        public Task EndAsync() => SendAsync("END");

        public async Task<RawPosition> GetPositionAsync()
        {
            var parts = (await SendAsync("POS")).Split(' ', 4);
            var c = CultureInfo.InvariantCulture;
            return new RawPosition(double.Parse(parts[0], c), double.Parse(parts[1], c), double.Parse(parts[2], c), parts[3]);
        }

        /// <returns>The monitor under the latest pen position, as counted by the plugin, and that position's age.</returns>
        public async Task<(int Index, int Count, double AgeMs)> GetScreenAsync()
        {
            var parts = (await SendAsync("SCREEN")).Split(' ');
            var c = CultureInfo.InvariantCulture;
            return (int.Parse(parts[0], c), int.Parse(parts[1], c), double.Parse(parts[2], c));
        }

        public Task ApplyAsync(CalibrationRecord record) => SendAsync("APPLY " + JsonSerializer.Serialize(record, CalibrationJson.Default.CalibrationRecord).ReplaceLineEndings(" "));

        public Task ClearAsync(string tablet) => SendAsync("CLEAR " + tablet);

        /// <returns>The reply after "OK ".</returns>
        private async Task<string> SendAsync(string line)
        {
            if (writer == null || reader == null)
                throw new IOException("Not connected to OpenTabletDriver.");

            await gate.WaitAsync();
            try
            {
                await writer.WriteLineAsync(line);
                var reply = await reader.ReadLineAsync() ?? throw new IOException("OpenTabletDriver closed the connection.");
                if (reply.StartsWith("ERR", StringComparison.Ordinal))
                    throw new DriverException(reply.Length > 4 ? reply[4..] : "Unknown error.");
                return reply.Length > 3 ? reply[3..] : "";
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            reader?.Dispose();
            writer?.Dispose();
            tcp?.Dispose();
            reader = null;
            writer = null;
            tcp = null;
        }
    }

    public class DriverException : Exception
    {
        public DriverException(string message) : base(message) { }
    }
}
