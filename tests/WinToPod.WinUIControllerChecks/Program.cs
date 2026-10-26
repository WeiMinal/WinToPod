// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using WinToPod.Desktop;
var checks = new List<string>();
void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
var ui = new FakeUi();
var controller = new PlaybackController(new DesktopSettings(), ui, false, enableVideoBridge: false, enableAutomation: false);
Check(ui.State is { Connected: false, Working: false, CanConnect: false } && ui.State.Entries.Count == 0, "UI controller starts offline without selecting or connecting a receiver");
controller.Mute();
Check(ui.State is { Volume: 20, Connected: false, Working: false }, "mute on a disconnected UI cannot start, disconnect or alter a session");
await controller.ConnectAsync("missing-receiver");
Check(ui.State is { Working: false }, "an unknown device key never opens a playback session");
await controller.DisconnectAsync();
Check(ui.PromptCancellations == 1 && ui.State is { Working: false }, "disconnect cancels UI prompts even before a session exists");
await controller.DisposeAsync();
Check(ui.Timers.Count == 3 && ui.Timers.All(t => t.Disposed && !t.Enabled), "shutdown stops and disposes all three frontend scheduling timers");
var cancellations = ui.PromptCancellations; await controller.DisposeAsync();
Check(ui.PromptCancellations == cancellations, "repeated shutdown does not re-enter connection cleanup");
var renders = ui.Renders; controller.SetVolume(37);
Check(ui.Renders == renders, "queued volume interaction after shutdown cannot render or issue volume commands");
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, production_controller_linked = true, winui_rendering_tested = false, homepod_tested = false }));

sealed class FakeUi : IPlaybackUi
{
    public DeviceViewState? State; public int Renders, PromptCancellations;
    public List<FakeTimer> Timers = [];
    public bool HasThreadAccess => true;
    public void Post(Action action) => action();
    public Task<T> InvokeAsync<T>(Func<Task<T>> operation) => operation();
    public IUiTimer CreateTimer(int intervalMs) { var timer = new FakeTimer(); Timers.Add(timer); return timer; }
    public void Render(DeviceViewState state) { State = state; Renders++; }
    public void Notify(string title, string message) => throw new InvalidOperationException("Unexpected notification");
    public void ShowPanel() { }
    public void ShowInterruption(string reason, string policy) => throw new InvalidOperationException("Unexpected interruption");
    public void CloseInterruption() { }
    public void CancelPrompts() => PromptCancellations++;
    public Task<PasswordAnswer?> RequestPasswordAsync(string device, bool playback, CancellationToken cancellation) => throw new InvalidOperationException("Unexpected pairing");
    public Task<bool?> RequestSingleFallbackAsync(string device, bool playback, CancellationToken cancellation) => throw new InvalidOperationException("Unexpected fallback");
}
sealed class FakeTimer : IUiTimer
{
    public event Action? Tick { add { } remove { } }
    public bool Enabled { get; private set; }
    public bool Disposed { get; private set; }
    public void Start() => Enabled = true;
    public void Stop() => Enabled = false;
    public void Dispose() { Stop(); Disposed = true; }
}
