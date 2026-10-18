// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinToPod.Core;

namespace WinToPod.Desktop;

/// <summary>Controls only the player instance explicitly launched by this app.</summary>
internal sealed class VideoPlayerSession : IDisposable
{
    private readonly Process _process;
    private readonly bool _vlc;
    private readonly string _pipe;
    private readonly HttpClient? _http;
    private readonly string _token;
    private readonly int _port;
    public uint ProcessId => (uint)_process.Id;
    public bool HasExited => _process.HasExited;
    public VideoPlayerSession(string player, string media, int delayMs)
    {
        _vlc = Path.GetFileName(player).Equals("vlc.exe", StringComparison.OrdinalIgnoreCase);
        _pipe = "WinToPod-" + Guid.NewGuid().ToString("N"); _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var info = new ProcessStartInfo(player) { UseShellExecute = false };
        foreach (var arg in VideoPlayerArguments.Create(player, media, delayMs, _vlc ? "" : @"\\.\pipe\" + _pipe, _port, _token)) info.ArgumentList.Add(arg);
        _process = Process.Start(info) ?? throw new IOException("播放器未能启动。");
        if (_vlc)
        {
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            _http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _token)));
        }
    }
    public async Task<bool> SetDelayAsync(int delayMs)
    {
        if (HasExited) return false;
        try
        {
            using var timeout = new CancellationTokenSource(2000);
            if (_vlc)
            {
                var value = (-Math.Clamp(delayMs, 0, 5000) / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                using var response = await _http!.GetAsync($"http://127.0.0.1:{_port}/requests/status.xml?command=audiodelay&val={value}", timeout.Token);
                return response.IsSuccessStatusCode;
            }
            using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { command = new object[] { "set_property", "audio-delay", -Math.Clamp(delayMs, 0, 5000) / 1000d }, request_id = 1 });
            await pipe.WriteAsync(request, timeout.Token); await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token); await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            // mpv can send unsolicited events before the matching reply.
            for (var i = 0; i < 32; i++)
            {
                var line = await reader.ReadLineAsync(timeout.Token); if (line is null || line.Length > 65536) return false;
                using var reply = JsonDocument.Parse(line);
                if (reply.RootElement.TryGetProperty("request_id", out var id) && id.GetInt32() == 1)
                    return reply.RootElement.GetProperty("error").GetString() == "success";
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException) { }
        return false;
    }
    public void Dispose() { _http?.Dispose(); _process.Dispose(); }
}
