// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Core;
namespace WinToPod.Desktop;
internal interface IPlaybackUi
{
    bool HasThreadAccess { get; }
    void Post(Action action);
    Task<T> InvokeAsync<T>(Func<Task<T>> operation);
    IUiTimer CreateTimer(int intervalMs);
    void Render(DeviceViewState state);
    void Notify(string title, string message);
    void ShowPanel();
    void ShowInterruption(string reason, string policy);
    void CloseInterruption();
    void CancelPrompts();
    Task<PasswordAnswer?> RequestPasswordAsync(string device, bool playback, CancellationToken cancellation);
    Task<bool?> RequestSingleFallbackAsync(string device, bool playback, CancellationToken cancellation);
}
internal interface IUiTimer : IDisposable
{
    event Action? Tick;
    bool Enabled { get; }
    void Start();
    void Stop();
}
internal sealed record PasswordAnswer(string Password, bool Remember);
internal sealed record Preferences(ConnectionPolicy Policy, bool StartAtLogin, bool AutoConnect, bool HideOnClose, bool PreferSingle);
internal sealed record CaptureChoice(string Name, string? Id) { public override string ToString() => Name; }
