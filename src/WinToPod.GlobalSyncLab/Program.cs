// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinToPod.GlobalSyncLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new LabWindow());
    }
}

internal sealed class LabWindow : Form
{
    private readonly ComboBox _screens = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new(500, 0) };
    private readonly Button _start = new() { Text = "开始全局画面实验", AutoSize = true, Enabled = false };
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Threading.Timer _watchdog;
    private Overlay? _overlay;
    private bool _polling, _closing;
    private int _delay;
    private long _heartbeat;
    private int _overlayActive;
    private long _lastResponse;
    private bool _connected;

    public LabWindow()
    {
        Text = "WinToPod · 全局画面补偿实验";
        ClientSize = new(560, 320); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(500, 0),
            Text = "跨应用延后所选显示器画面，声音由正在运行的 WinToPod 发送。\n这是观看用实验，画质暂限960×540、约15fps；不会降低音频延迟。\n操作电脑前按 Ctrl+Alt+F8 退出。画面仅存内存，不保存、不上传。\nDRM、HDR、独占全屏及系统安全桌面不在本轮支持范围。" });
        foreach (var screen in Screen.AllScreens) _screens.Items.Add(screen);
        _screens.DisplayMember = nameof(Screen.DeviceName);
        if (_screens.Items.Count > 0) _screens.SelectedIndex = 0;
        layout.Controls.Add(_screens); layout.Controls.Add(_status); layout.Controls.Add(_start);
        Controls.Add(layout);
        _start.Click += (_, _) => StartOverlay();
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _heartbeat = _clock.ElapsedMilliseconds;
        // A stuck capture must not leave a stale full-screen surface indefinitely.
        // Terminate this helper only; main WinToPod audio is a separate process.
        _watchdog = new(_ =>
        {
            if (Volatile.Read(ref _overlayActive) == 1 && _clock.ElapsedMilliseconds - Interlocked.Read(ref _heartbeat) > 4000)
                Environment.Exit(2);
        }, null, 1000, 1000);
    }
    private async Task PollAsync()
    {
        Interlocked.Exchange(ref _heartbeat, _clock.ElapsedMilliseconds);
        if (_polling || _closing) return;
        _polling = true;
        try
        {
            using var response = await _http.GetAsync("http://127.0.0.1:38765/video-sync", HttpCompletionOption.ResponseHeadersRead, _lifetime.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException();
            await response.Content.LoadIntoBufferAsync(16384, _lifetime.Token);
            var body = await response.Content.ReadAsByteArrayAsync(_lifetime.Token);
            if (body.Length > 16384) throw new IOException();
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.GetProperty("protocol").GetString() != "WinToPod.VideoSync/1") throw new IOException();
            var connected = root.GetProperty("connected").GetBoolean();
            var delay = root.GetProperty("delayMs").GetInt32();
            if (delay is < 0 or > 5000) throw new IOException();
            if (_closing) return;
            _connected = connected; _lastResponse = _clock.ElapsedMilliseconds;
            if (delay != _delay) { StopOverlay(); _delay = delay; }
            if (!connected) StopOverlay();
            _start.Enabled = connected && delay > 0 && _overlay is null;
            _status.Text = connected ? $"音箱在线 · 建议延后 {_delay}ms（协议估算，非实测声学延迟）" : "请先在 WinToPod 连接音箱。";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            if (_closing) return;
            _connected = false; StopOverlay(); _start.Enabled = false;
            _status.Text = "主程序未响应或状态无效，已恢复原画面。";
        }
        finally { _polling = false; }
    }
    private void StartOverlay()
    {
        if (!_connected || _delay <= 0 || _overlay is not null || _screens.SelectedItem is not Screen screen) return;
        if (!Native.RegisterHotKey(Handle, 1, 0x4003, 0x77))
        { _status.Text = "退出快捷键 Ctrl+Alt+F8 被占用，未开始实验。"; return; }
        try
        {
            _overlay = new(screen.Bounds, _delay, _clock, () =>
            {
                Interlocked.Exchange(ref _heartbeat, _clock.ElapsedMilliseconds);
                return _connected && _clock.ElapsedMilliseconds - _lastResponse < 1800;
            }, () => BeginInvoke(new Action(StopOverlay)));
            _overlay.Begin();
            Volatile.Write(ref _overlayActive, 1);
            _start.Enabled = false;
        }
        catch
        {
            StopOverlay(); _status.Text = "无法启动屏幕捕获或排除自身窗口，已保留原画面。";
        }
    }
    private void StopOverlay()
    {
        Volatile.Write(ref _overlayActive, 0);
        var overlay = _overlay; _overlay = null;
        overlay?.Dispose();
        if (IsHandleCreated) Native.UnregisterHotKey(Handle, 1);
        _start.Enabled = !_closing && _connected && _delay > 0;
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == 1) { StopOverlay(); return; }
        // Session end or display change ends this experiment rather than recapturing another monitor.
        if (message.Msg is 0x0016 or 0x007E) StopOverlay();
        base.WndProc(ref message);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true; _lifetime.Cancel(); _poll.Stop(); StopOverlay();
        _watchdog.Dispose(); _poll.Dispose(); _http.Dispose();
        base.OnFormClosed(e);
    }
}

internal sealed class Overlay : Form
{
    private readonly Rectangle _source;
    private readonly int _delay;
    private readonly Stopwatch _clock;
    private readonly Func<bool> _alive;
    private readonly Action _stop;
    private readonly DelayQueue<Bitmap> _frames = new(80);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 67 };
    private readonly Size _frameSize;
    private Bitmap? _displayed;
    private bool _disposed;
    private long _lastTick;

    public Overlay(Rectangle source, int delay, Stopwatch clock, Func<bool> alive, Action stop)
    {
        _source = source; _delay = delay; _clock = clock; _alive = alive; _stop = stop;
        var scale = Math.Min(1d, Math.Min(960d / source.Width, 540d / source.Height));
        _frameSize = new(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Bounds = source; BackColor = Color.Black; DoubleBuffered = true;
        _tick.Tick += (_, _) => CaptureFrame();
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } // no activate, layered, tool, click-through
    }
    public void Begin()
    {
        if (!Native.SetWindowDisplayAffinity(Handle, 0x11) ||
            !Native.SetLayeredWindowAttributes(Handle, 0, 255, 2))
            throw new InvalidOperationException("Capture exclusion unavailable.");
        _lastTick = _clock.ElapsedMilliseconds;
        _tick.Start();
    }
    private void CaptureFrame()
    {
        if (_disposed) return;
        var now = _clock.ElapsedMilliseconds;
        if (!_alive() || now - _lastTick > 1000 || !Screen.AllScreens.Any(s => s.Bounds == _source))
        { _tick.Stop(); Hide(); _stop(); return; }
        _lastTick = now;
        try
        {
            var image = new Bitmap(_frameSize.Width, _frameSize.Height, PixelFormat.Format32bppRgb);
            try
            {
                using var graphics = Graphics.FromImage(image);
                var target = graphics.GetHdc(); var source = Native.GetDC(IntPtr.Zero);
                try
                {
                    if (source == IntPtr.Zero || !Native.StretchBlt(target, 0, 0, image.Width, image.Height,
                        source, _source.X, _source.Y, _source.Width, _source.Height, 0x40CC0020))
                        throw new InvalidOperationException("Capture unavailable.");
                }
                finally { if (source != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, source); graphics.ReleaseHdc(target); }
            }
            catch { image.Dispose(); throw; }
            _frames.Push(_clock.ElapsedMilliseconds, image);
            var due = _frames.TakeDue(_clock.ElapsedMilliseconds, _delay);
            if (due is { } frame)
            {
                _displayed?.Dispose(); _displayed = frame.Value;
                if (!Visible) Show();
                Invalidate();
            }
        }
        catch { _tick.Stop(); Hide(); _stop(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (_displayed is not null) e.Graphics.DrawImage(_displayed, ClientRectangle);
        using var brush = new SolidBrush(Color.FromArgb(200, Color.Black));
        e.Graphics.FillRectangle(brush, 0, 0, 470, 28);
        e.Graphics.DrawString($"全局补偿实验 · {_delay}ms · Ctrl+Alt+F8 退出", Font, Brushes.White, 8, 6);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        { _disposed = true; _tick.Stop(); _tick.Dispose(); _frames.Dispose(); _displayed?.Dispose(); _displayed = null; }
        base.Dispose(disposing);
    }
}
internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
