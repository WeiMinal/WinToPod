// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Dispatching;
using WinToPod.Desktop;
namespace WinToPod.WinUI;
internal sealed class DispatcherTimer : IUiTimer
{
    private readonly DispatcherQueueTimer _timer;
    public event Action? Tick;
    public bool Enabled => _timer.IsRunning;
    public DispatcherTimer(DispatcherQueue dispatcher, int interval)
    { _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(interval); _timer.Tick += OnTick; }
    private void OnTick(DispatcherQueueTimer sender, object args) => Tick?.Invoke();
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Dispose() { Stop(); _timer.Tick -= OnTick; }
}
