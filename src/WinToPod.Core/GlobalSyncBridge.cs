// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WinToPod.Core;

public sealed record GlobalSyncState(bool Connected, int DelayMs)
{
    public static GlobalSyncState Offline { get; } = new(false, 0);
    public static int FromFrames(int frames, int correctionMs = 0) => (int)Math.Clamp(Math.Round(frames * 1000d / 44100) + correctionMs, 0d, 5000d);
}

/// <summary>Read-only loopback timing for the global display helper. No receiver control API.</summary>
public sealed class GlobalSyncBridge : IDisposable
{
    public const int DefaultPort = 38765;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private GlobalSyncState _state = GlobalSyncState.Offline;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public void Update(GlobalSyncState state) => Volatile.Write(ref _state, state with { DelayMs = Math.Clamp(state.DelayMs, 0, 5000) });
    public GlobalSyncBridge(int port = DefaultPort)
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
        var lengths = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var chunked = lines.Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase));
        var length = 0;
        var boundedBody = !chunked && lengths.Length == 1 &&
            int.TryParse(lengths[0][15..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) &&
            length is >= 0 and <= 768;
        // Consume bounded request bodies even when the method or origin is rejected.
        // Closing with unread bytes can reset the TCP connection on Windows and lose the 400 response.
        var payload = boundedBody ? new byte[length] : Array.Empty<byte>();
        if (payload.Length > 0) await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        var host = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
        var valid = lines[0] == "GET /global-sync HTTP/1.1" && host == $"127.0.0.1:{Port}" &&
            !lines.Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        // Native helper only. Browser origins and retired extension APIs are not accepted.
        valid = valid && lines.Count(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) == 1 &&
            !lines.Any(l => l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase));
        var snapshot = Volatile.Read(ref _state);
        var body = valid ? JsonSerializer.SerializeToUtf8Bytes(new { protocol = "WinToPod.GlobalSync/1", connected = snapshot.Connected, delayMs = snapshot.DelayMs }) : "{}"u8.ToArray();
        var response = $"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n" + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).ConfigureAwait(false); await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } _stop.Dispose(); }
}
