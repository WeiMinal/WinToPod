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
        string? lastState = null;
        void Report(string state)
        {
            if (state == lastState) return;
            lastState = state; diagnostic("媒体信息状态：" + state);
        }
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                ReadResult read;
                try
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    read = await ReadAsync(manager, readTimeout.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch
                {
                    await target.PublishMetadataAsync(null, cancellation);
                    diagnostic("媒体信息：读取失败；已请求清空并停用本次同步。"); return;
                }
                await target.PublishMetadataAsync(read.Metadata, cancellation);
                Report(read.Metadata is { } value
                    ? $"已读取音乐；封面={(value.Jpeg is null ? "无" : "有")}；已交给能力门控发送链路，接收端显示待验证"
                    : read.State + "；已请求清空，接收端效果待验证");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { diagnostic("媒体信息：发送已停止；连接状态由原有保护处理。"); }
    }

    private sealed record ReadResult(PlaybackMetadata? Metadata, string State);
    private static async Task<ReadResult> ReadAsync(GlobalSystemMediaTransportControlsSessionManager manager, CancellationToken cancellation)
    {
        // CurrentSession alone can refer to a paused or unrelated player. Multiple
        // playing sessions cannot describe the mixed desktop audio unambiguously.
        var playing = manager.GetSessions().Where(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToArray();
        if (playing.Length != 1) return new(null, playing.Length == 0 ? "无正在播放的系统媒体会话" : "多个媒体会话同时播放");
        var session = playing[0];
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellation);
        if (properties.PlaybackType != Windows.Media.MediaPlaybackType.Music) return new(null, "来源未声明音乐类型");
        if (string.IsNullOrWhiteSpace(properties.Title)) return new(null, "音乐来源未提供标题");
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
        if (current.Length != 1 || current[0].SourceAppUserModelId != session.SourceAppUserModelId) return new(null, "读取期间来源已切换");
        var verify = await current[0].TryGetMediaPropertiesAsync().AsTask(cancellation);
        if (verify.PlaybackType != Windows.Media.MediaPlaybackType.Music || verify.Title != properties.Title || verify.Artist != properties.Artist || verify.AlbumTitle != properties.AlbumTitle) return new(null, "读取期间曲目已变化");
        var timeline = current[0].GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        var position = MediaTimeline.Position(timeline.Position - timeline.StartTime, duration,
            timeline.LastUpdatedTime, DateTimeOffset.UtcNow, current[0].GetPlaybackInfo().PlaybackRate);
        return new(new(properties.Title, properties.Artist, properties.AlbumTitle, jpeg, position, duration), "ready");
    }

    private static async Task<byte[]?> ReadJpegAsync(IRandomAccessStreamReference thumbnail, CancellationToken cancellation)
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
