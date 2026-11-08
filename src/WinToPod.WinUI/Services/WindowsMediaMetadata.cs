// SPDX-License-Identifier: GPL-3.0-or-later
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinToPod.AirPlay.Media;
using WinToPod.Core;

namespace WinToPod.WinUI.Services;

// Optional, connection-scoped reader. Never changes player state or logs metadata.
internal static class WindowsMediaMetadata
{
    internal static async Task RunAsync(NativePlaybackSession target, Action<string> diagnostic, CancellationToken cancellation)
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch { diagnostic("媒体信息：系统读取不可用；已停用本次同步。"); return; }
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                PlaybackMetadata? metadata;
                try { metadata = await ReadAsync(manager, cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch
                {
                    await target.PublishMetadataAsync(null, cancellation);
                    diagnostic("媒体信息：读取失败；已清空并停用本次同步。"); return;
                }
                await target.PublishMetadataAsync(metadata, cancellation);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { diagnostic("媒体信息：发送已停止；连接状态由原有保护处理。"); }
    }

    private static async Task<PlaybackMetadata?> ReadAsync(GlobalSystemMediaTransportControlsSessionManager manager, CancellationToken cancellation)
    {
        // CurrentSession alone can refer to a paused or unrelated player. Multiple
        // playing sessions cannot describe the mixed desktop audio unambiguously.
        var playing = manager.GetSessions().Where(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToArray();
        if (playing.Length != 1) return null;
        var session = playing[0];
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellation);
        if (string.IsNullOrWhiteSpace(properties.Title)) return null;
        byte[]? jpeg = null;
        if (properties.Thumbnail is { } thumbnail)
        {
            try { jpeg = await ReadJpegAsync(thumbnail, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { /* Invalid/oversized art must not suppress track text. */ }
        }
        // Recheck after asynchronous thumbnail reads to avoid publishing a closed
        // or switched session. The next poll supplies the new session's data.
        var current = manager.GetSessions().Where(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToArray();
        if (current.Length != 1 || current[0].SourceAppUserModelId != session.SourceAppUserModelId) return null;
        var verify = await session.TryGetMediaPropertiesAsync().AsTask(cancellation);
        if (verify.Title != properties.Title || verify.Artist != properties.Artist || verify.AlbumTitle != properties.AlbumTitle) return null;
        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        var position = MediaTimeline.Position(timeline.Position - timeline.StartTime, duration,
            timeline.LastUpdatedTime, DateTimeOffset.UtcNow, session.GetPlaybackInfo().PlaybackRate);
        return new(properties.Title, properties.Artist, properties.AlbumTitle, jpeg, position, duration);
    }

    private static async Task<byte[]?> ReadJpegAsync(RandomAccessStreamReference thumbnail, CancellationToken cancellation)
    {
        using var source = await thumbnail.OpenReadAsync().AsTask(cancellation);
        if (source.Size is 0 or > 4 * 1024 * 1024) return null;
        var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellation);
        if (decoder.PixelWidth is 0 or > 4096 || decoder.PixelHeight is 0 or > 4096) return null;
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask(cancellation);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output).AsTask(cancellation);
        encoder.SetSoftwareBitmap(bitmap);
        var scale = Math.Min(1, 512d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        encoder.BitmapTransform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale));
        encoder.BitmapTransform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
        await encoder.FlushAsync().AsTask(cancellation);
        if (output.Size is 0 or > 1024 * 1024) return null;
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var count = checked((uint)output.Size);
        if (await reader.LoadAsync(count).AsTask(cancellation) != count) return null;
        var bytes = new byte[count]; reader.ReadBytes(bytes); return bytes;
    }
}
