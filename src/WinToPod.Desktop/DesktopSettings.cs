// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Microsoft.Win32;
using WinToPod.Core;

namespace WinToPod.Desktop;

internal enum ApplicationAppearance { System, Light, Dark }

internal sealed record DesktopSettings
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Schema { get; init; } = 1;
    public string? SelectedReceiver { get; set; }
    public string? LastConnectedReceiver { get; set; }
    public string[] LastConnectedMembers { get; set; } = [];
    public ConnectionPolicy Policy { get; set; } = ConnectionPolicy.Conservative;
    public bool AutoConnectOnStart { get; set; } = true;
    public string SenderIdentity { get; init; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
    public Dictionary<string, DateTimeOffset> ReceiverRetryAfter { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> WinUiPreMasterTargets { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> SingleFallbackTargets { get; init; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonRequired]
    public Dictionary<string, int> Volumes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> UnmutedVolumes { get; init; } = new(StringComparer.Ordinal);
    public bool HideOnClose { get; set; }
    public ApplicationAppearance WinUiAppearance { get; set; } = ApplicationAppearance.System;
    public string? CaptureEndpointId { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public bool Interrupted { get; set; }
    public bool NoticeDismissed { get; set; }
    public string? InterruptedReceiver { get; set; }
    public string InterruptionReason { get; set; } = "unknown";
    public string? VideoPlayerPath { get; set; }
    public Dictionary<string, int> VideoCorrectionsMs { get; init; } = new(StringComparer.Ordinal);
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinToPod");
    public static string FilePath => Path.Combine(DirectoryPath, "desktop-settings.json");
    public static DesktopSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException();
            if (!Enum.IsDefined(settings.Policy) || !Enum.IsDefined(settings.WinUiAppearance) || settings.LastConnectedMembers is null || settings.LastConnectedMembers.Length > 2 || settings.LastConnectedMembers.Any(string.IsNullOrWhiteSpace) ||
                settings.LastConnectedReceiver is { } target && (target.Length > 4096 || string.IsNullOrWhiteSpace(target) || target.Any(char.IsControl)) ||
                settings.ReceiverRetryAfter is null || settings.ReceiverRetryAfter.Count > 1024 || settings.ReceiverRetryAfter.Keys.Any(string.IsNullOrWhiteSpace) || settings.Schema != 1 || !Guid.TryParse(settings.SenderIdentity, out _) || settings.SingleFallbackTargets is null ||
                settings.WinUiPreMasterTargets is null || settings.WinUiPreMasterTargets.Count > 1024 || settings.WinUiPreMasterTargets.Any(string.IsNullOrWhiteSpace) ||
                settings.SingleFallbackTargets.Count > 1024 || settings.SingleFallbackTargets.Any(string.IsNullOrWhiteSpace) || settings.Volumes is null || settings.Volumes.Count > 1024 ||
                settings.UnmutedVolumes is null || settings.UnmutedVolumes.Count > 1024 || settings.UnmutedVolumes.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < 1 or > 100) ||
                settings.Volumes.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < 0 or > 100) ||
                settings.CaptureEndpointId is { } capture && (capture.Length > 4096 || string.IsNullOrWhiteSpace(capture) || capture.Any(char.IsControl)) ||
                settings.VideoPlayerPath is { } player && (player.Length > 4096 || player.Any(char.IsControl)) ||
                settings.VideoCorrectionsMs is null || settings.VideoCorrectionsMs.Count > 1024 || settings.VideoCorrectionsMs.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < -1500 or > 1500) ||
                settings.Interrupted && string.IsNullOrWhiteSpace(settings.InterruptedReceiver))
                throw new InvalidDataException();
            // Older builds only remembered selection and confirmed volume, not
            // successful playback. Do not guess an automatic target from them.
            return settings;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { throw new IOException("设置文件损坏或版本不兼容。原文件已保留；为避免错误恢复音量，WinToPod不会继续连接。", e); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath); var temporary = FilePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, this, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartsAtLogin()
    { using var key = Registry.CurrentUser.OpenSubKey(RunPath); return key?.GetValue("WinToPod") is string; }
    public static void SetStartAtLogin(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath, true) ?? throw new IOException("无法打开当前用户登录启动设置。");
        if (enabled) key.SetValue("WinToPod", "\"" + (executablePath ?? Environment.ProcessPath ?? throw new IOException("无法确定程序路径。")) + "\" --startup", RegistryValueKind.String);
        else key.DeleteValue("WinToPod", false);
    }
}
