// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinToPod.Core;

namespace WinToPod.Desktop;

internal sealed class LoopbackAudioSource : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private ProcessAudioCapture? _processCapture;
    private readonly ClockAlignedPcmBuffer? _rateBuffer;
    public CaptureRateSnapshot? RateSnapshot => _rateBuffer?.Snapshot;
    public string CaptureMode { get; private set; } = "endpoint";
    public string? CaptureFallback { get; private set; }
    public long ReceivedFrames { get; private set; }
    public long Discontinuities { get; private set; }
    private readonly MMDevice _device;
    private readonly PcmBuffer _buffer = new();
    private readonly object _timingGate = new(); private readonly CaptureDelayEstimator _captureDelay = new(); private long _lastCaptureTimestamp;
    public int EstimatedCaptureLagMs { get { lock (_timingGate) return _captureDelay.DelayMs; } }
    public int BufferedFrames => RateSnapshot?.Buffer.BufferedFrames ?? _buffer.AvailableBytes / 4;
    private Exception? _error;
    private bool _disposed;
    public long DroppedFrames => RateSnapshot?.Buffer.DroppedFrames ?? _buffer.DroppedFrames;
    public long MissingFrames => RateSnapshot?.Buffer.MissingFrames ?? _buffer.MissingFrames;
    public double Peak { get; private set; }
    public string EndpointId => _device.ID;

    public LoopbackAudioSource(string? endpointId) : this(endpointId, true) { }
    private LoopbackAudioSource(string? endpointId, bool startEndpoint, bool clockAligned = false)
    {
        if (clockAligned) _rateBuffer = new();
        using var enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(endpointId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(endpointId);
        try { if (startEndpoint) StartEndpoint(); } catch { _device.Dispose(); throw; }
    }
    public static async Task<LoopbackAudioSource> OpenAsync(string? endpointId, CancellationToken cancellation, bool requirePreMaster = false, bool clockAligned = false)
    {
        var source = new LoopbackAudioSource(endpointId, false, clockAligned);
        try
        {
            if ((endpointId is null || requirePreMaster) && ProcessAudioCapture.Supported)
            {
                try
                {
                    source._processCapture = await ProcessAudioCapture.OpenAsync(source.Accept, e => Interlocked.CompareExchange(ref source._error, e, null), cancellation);
                    source.CaptureMode = "application-mix-pre-master"; return source;
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or TimeoutException or PlatformNotSupportedException)
                { if (requirePreMaster) throw new IOException("无法启动应用声音采集。为避免音量重复衰减，本次不会切换到输出设备回环采集。", e); source.CaptureFallback = e is System.Runtime.InteropServices.COMException com ? $"COM 0x{com.HResult:X8}" : e.GetType().Name; source._error = null; source._buffer.Clear(); }
            }
            if (requirePreMaster) throw new PlatformNotSupportedException("应用声音采集需要 Windows 11 或 Windows build 20348 及以上。请更新系统后重试。");
            source.StartEndpoint(); return source;
        }
        catch { source.Dispose(); throw; }
    }
    private void StartEndpoint()
    {
        try
        {
            _capture = new WasapiLoopbackCapture(_device) { WaveFormat = new WaveFormat(44100, 16, 2) };
            _capture.DataAvailable += (_, e) => { if (e.BytesRecorded > 0) Accept(e.Buffer.AsSpan(0, e.BytesRecorded - e.BytesRecorded % 4).ToArray(), false); };
            _capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Interlocked.CompareExchange(ref _error, e.Exception, null); };
            _capture.StartRecording();
            if (_capture.WaveFormat.SampleRate != 44100 || _capture.WaveFormat.Channels != 2 || _capture.WaveFormat.BitsPerSample != 16)
                throw new IOException("系统输出不支持当前要求的44.1kHz/16bit/双声道采集格式。");
        }
        catch { _capture?.Dispose(); _capture = null; throw; }
    }
    private void Accept(byte[] copy, bool discontinuity)
    {
        if (_disposed || copy.Length == 0) return;
        double peak = 0;
        for (var at = 0; at + 1 < copy.Length; at += 2) peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(copy.AsSpan(at)) / 32768d));
        Peak = peak;
        lock (_timingGate)
        {
            if (_rateBuffer is { } rate) { if (discontinuity) rate.Clear(); rate.Write(copy); } else _buffer.Write(copy); ReceivedFrames += copy.Length / 4; if (discontinuity) Discontinuities++;
            _lastCaptureTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
    public ValueTask ReadAsync(Memory<byte> destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_error is not null) throw new IOException("Windows系统声音采集已中断。", _error);
        lock (_timingGate) { if (_lastCaptureTimestamp != 0) _captureDelay.Observe(BufferedFrames, System.Diagnostics.Stopwatch.GetElapsedTime(_lastCaptureTimestamp).TotalMilliseconds); if (_rateBuffer is { } rate) rate.Read(destination.Span); else _buffer.Read(destination.Span); }
        return ValueTask.CompletedTask;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _processCapture?.Dispose(); _capture?.StopRecording(); _capture?.Dispose(); _device.Dispose(); _buffer.Clear(); _rateBuffer?.Clear();
    }
}
