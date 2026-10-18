// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace WinToPod.Core;

public static class VideoPlayerArguments
{
    public static string[] Create(string playerPath, string mediaPath, int delayMs, string channel, int port, string token)
    {
        if (delayMs is < 0 or > 5000) throw new ArgumentOutOfRangeException(nameof(delayMs));
        if (!Path.IsPathFullyQualified(mediaPath) || mediaPath.Any(char.IsControl)) throw new ArgumentException("需要完整的视频文件路径。");
        return Path.GetFileName(playerPath).ToLowerInvariant() switch
        {
            "vlc.exe" => ["--no-one-instance", "--audio-desync=" + (-delayMs).ToString(CultureInfo.InvariantCulture), "--extraintf=http", "--http-host=127.0.0.1", "--http-port=" + port.ToString(CultureInfo.InvariantCulture), "--http-password=" + token, mediaPath],
            "mpv.exe" => ["--audio-delay=" + (-delayMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "--input-ipc-server=" + channel, "--", mediaPath],
            _ => throw new ArgumentException("请选择 VLC（vlc.exe）或 mpv（mpv.exe）。")
        };
    }
}
