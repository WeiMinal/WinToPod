// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace WinToPod.AirPlay.Transport;

// The receiver initiates encrypted requests over the event TCP connection.
// Acknowledge messages, never execute an arbitrary command or log its content.
public static class ReceiverEventChannel
{
    public static async Task RunAsync(Stream stream, Action<string>? diagnostic, CancellationToken cancellation)
    {
        long messages = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var header = new List<byte>(512); var one = new byte[1]; var end = 0;
            while (end != 4)
            {
                if (header.Count >= 16384) throw new InvalidDataException("Event request header exceeds limit.");
                await stream.ReadExactlyAsync(one, cancellation).ConfigureAwait(false); header.Add(one[0]);
                end = (end, one[0]) switch { (0, 13) => 1, (1, 10) => 2, (2, 13) => 3, (3, 10) => 4, (_, 13) => 1, _ => 0 };
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] is not ("RTSP/1.0" or "HTTP/1.1") || first[0] is not ("POST" or "GET" or "OPTIONS"))
                throw new InvalidDataException("Unsupported event request.");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(x => x.Length > 0))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) throw new InvalidDataException("Invalid event request header.");
            }
            if (fields.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("Unsupported event transfer encoding.");
            var length = 0;
            if (fields.TryGetValue("Content-Length", out var size) && (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length is < 0 or > 2 * 1024 * 1024))
                throw new InvalidDataException("Invalid event request length.");
            // Drain a bounded body without retaining private metadata or commands.
            var scratch = new byte[Math.Min(4096, length)];
            for (var remaining = length; remaining > 0;)
            {
                var take = Math.Min(remaining, scratch.Length);
                await stream.ReadExactlyAsync(scratch.AsMemory(0, take), cancellation).ConfigureAwait(false); remaining -= take;
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(scratch);
            var sequence = "";
            if (fields.TryGetValue("CSeq", out var cseq))
            {
                if (cseq.Length is < 1 or > 10 || !uint.TryParse(cseq, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("Invalid event CSeq.");
                sequence = "CSeq: " + cseq + "\r\n";
            }
            // Minimal empty acknowledgement; no Audio-Latency header, body or
            // extra playback command. Mirrors the request's protocol and CSeq.
            var response = Encoding.ASCII.GetBytes(first[2] + " 200 OK\r\nServer: WinToPod\r\n" + sequence + "\r\n");
            await stream.WriteAsync(response, cancellation).ConfigureAwait(false); await stream.FlushAsync(cancellation).ConfigureAwait(false);
            messages++;
            if (messages == 1 || messages % 100 == 0) diagnostic?.Invoke($"事件通道已解密并确认接收：messages={messages}；不记录事件内容。");
        }
    }
}
