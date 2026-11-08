// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinToPod.AirPlay.Media;

// Ephemeral data only. Never include values in diagnostics or persist to disk.
public sealed record PlaybackMetadata(string Title, string Artist, string Album, byte[]? Jpeg = null,
    TimeSpan? Position = null, TimeSpan? Duration = null);

internal sealed class MetadataPublisher
{
    internal const int MaximumArtworkBytes = 1024 * 1024;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HashSet<string> _disabled = [];
    private readonly Dictionary<string, string> _sent = [];

    internal async Task PublishAsync(PlaybackMetadata? value, ReceiverCapabilities capabilities, uint timestamp,
        Func<string, byte[], uint, CancellationToken, Task<int>> send, CancellationToken cancellation)
    {
        if (!capabilities.Known) return;
        var text = EncodeText(value);
        var artwork = value?.Jpeg;
        if (artwork is not null && (artwork.Length > MaximumArtworkBytes || artwork.Length < 4 ||
            artwork[0] != 0xff || artwork[1] != 0xd8 || artwork[^2] != 0xff || artwork[^1] != 0xd9))
            throw new ArgumentException("封面格式或大小无效。", nameof(value));
        await _serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (capabilities.TrackText) await Part("text", "application/x-dmap-tagged", text);
            if (capabilities.Artwork) await Part("art", artwork is null ? "image/none" : "image/jpeg", artwork ?? []);
            if (capabilities.Progress)
            {
                var progress = Progress(value, timestamp);
                await Part("progress", "text/parameters", progress);
            }
        }
        finally { _serial.Release(); }

        async Task Part(string key, string type, byte[] body)
        {
            if (_disabled.Contains(key)) return;
            var fingerprint = Convert.ToHexString(SHA256.HashData(body));
            if (_sent.GetValueOrDefault(key) == fingerprint) return;
            cancellation.ThrowIfCancellationRequested();
            var status = await send(type, body, timestamp, cancellation).ConfigureAwait(false);
            if (status == 200) _sent[key] = fingerprint;
            else if (status is 400 or 415 or 451 or 501) _disabled.Add(key);
            else throw new IOException($"媒体信息被拒绝（RTSP {status}）。");
        }
    }

    internal static byte[] EncodeText(PlaybackMetadata? value)
    {
        using var children = new MemoryStream();
        foreach (var (tag, field) in new[] { ("minm", value?.Title), ("asar", value?.Artist), ("asal", value?.Album) })
        {
            // A rune limit avoids splitting surrogate pairs and bounds UTF-8 payloads.
            var builder = new StringBuilder();
            foreach (var rune in (field ?? "").EnumerateRunes().Take(512)) builder.Append(rune);
            Write(children, tag, Encoding.UTF8.GetBytes(builder.ToString()));
        }
        using var result = new MemoryStream(); Write(result, "mlit", children.ToArray()); return result.ToArray();
    }
    private static void Write(Stream stream, string tag, byte[] bytes)
    {
        stream.Write(Encoding.ASCII.GetBytes(tag));
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
        stream.Write(size); stream.Write(bytes);
    }
    internal static byte[] Progress(PlaybackMetadata? value, uint timestamp)
    {
        if (value?.Position is not { } position || value.Duration is not { } duration ||
            position < TimeSpan.Zero || duration <= TimeSpan.Zero || position > duration || duration.TotalSeconds * 44100 >= uint.MaxValue)
            return "progress: 0/0/0\r\n"u8.ToArray();
        var start = unchecked(timestamp - (uint)(position.TotalSeconds * 44100));
        var end = unchecked(start + (uint)(duration.TotalSeconds * 44100));
        return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"progress: {start}/{timestamp}/{end}\r\n"));
    }
}
