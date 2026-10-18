// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WinToPod.Core;

public sealed record VideoSyncState(bool Connected, int DelayMs)
{
    public static VideoSyncState Offline { get; } = new(false, 0);
    public static int FromFrames(int frames, int correctionMs = 0) => Math.Clamp((int)Math.Round(frames * 1000d / 44100) + correctionMs, 0, 5000);
}

public sealed record VideoCompanionStatus(bool Detected, string Phase, int ObservedDelayMs, long FramesPresented, string Reason)
{ public static VideoCompanionStatus Missing { get; } = new(false, "not-detected", 0, 0, "none"); }

/// <summary>Read-only loopback status for the optional video companion. No receiver control API.</summary>
public sealed class VideoSyncBridge : IDisposable
{
    public const int DefaultPort = 38765;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private VideoSyncState _state = VideoSyncState.Offline;
    private sealed record Heartbeat(VideoCompanionStatus Status, long At);
    private Heartbeat? _heartbeat;
    public VideoCompanionStatus CompanionStatus { get { var h = Volatile.Read(ref _heartbeat); return h is not null && Environment.TickCount64 - h.At < 5000 ? h.Status : VideoCompanionStatus.Missing; } }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public void Update(VideoSyncState state) => Volatile.Write(ref _state, state with { DelayMs = Math.Clamp(state.DelayMs, 0, 5000) });
    public VideoSyncBridge(int port = DefaultPort)
    {
        _listener = new(IPAddress.Loopback, port); _listener.Server.ExclusiveAddressUse = true; _listener.Start(8);
        _loop = ServeAsync();
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(2000);
                try { await RespondAsync(client.GetStream(), timeout.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task RespondAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new List<byte>(1024); var next = new byte[1];
        while (header.Count < 8192)
        {
            if (await stream.ReadAsync(next, token).ConfigureAwait(false) == 0) return;
            header.Add(next[0]); var n = header.Count;
            if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var host = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
        var valid = lines[0] == "GET /video-sync HTTP/1.1" && host == $"127.0.0.1:{Port}" &&
            !lines.Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var origin = lines.FirstOrDefault(l => l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))?[7..].Trim();
        var extensionOrigin = origin is { Length: 51 } && origin.StartsWith("chrome-extension://", StringComparison.Ordinal) && origin[19..].All(c => c is >= 'a' and <= 'p');
        var accepted = false;
        if (lines[0] == "POST /video-sync/companion HTTP/1.1" && extensionOrigin && host == $"127.0.0.1:{Port}" &&
            lines.Count(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) == 1 && lines.Count(l => l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) == 1 &&
            !lines.Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) && lines.Count(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) == 1 &&
            int.TryParse(lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))?[15..].Trim(), out var length) && length is > 0 and <= 768)
        {
            var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            try
            {
                using var report = JsonDocument.Parse(payload); var value = report.RootElement;
                var phase = value.GetProperty("phase").GetString(); var reason = value.GetProperty("reason").GetString();
                var observed = value.GetProperty("observedDelayMs").GetInt32(); var frames = value.GetProperty("framesPresented").GetInt64();
                if (value.EnumerateObject().Count() == 4 && phase is "idle" or "waiting" or "buffering" or "active" or "paused" or "unsupported" or "error" &&
                    reason is "none" or "no-video" or "drm" or "native-controls" or "no-codec" or "cross-origin" or "codec-error" or "buffer-limit" or "fullscreen" or "hidden" && observed is >= 0 and <= 10000 && frames is >= 0 and <= 1000000000)
                { Volatile.Write(ref _heartbeat, new(new(true, phase!, observed, frames, reason!), Environment.TickCount64)); accepted = true; }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        }
        var snapshot = Volatile.Read(ref _state);
        var body = valid ? JsonSerializer.SerializeToUtf8Bytes(new { protocol = "WinToPod.VideoSync/1", connected = snapshot.Connected, delayMs = snapshot.DelayMs }) : "{}"u8.ToArray();
        var response = $"HTTP/1.1 {(valid || accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n" + (extensionOrigin ? $"Access-Control-Allow-Origin: {origin}\r\n" : "") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).ConfigureAwait(false); await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } _stop.Dispose(); }
}
