// SPDX-License-Identifier: GPL-3.0-or-later
// Test substitute for the Windows device boundary only. AudioSources.cs is linked unchanged.
namespace NAudio.CoreAudioApi
{
    public enum DataFlow { Render }
    public enum Role { Multimedia }
    public sealed class MMDevice : IDisposable
    {
        public static MMDevice Latest { get; private set; } = null!;
        public MMDevice() => Latest = this;
        public string ID => "synthetic-capture";
        public AudioEndpointVolume AudioEndpointVolume { get; } = new();
        public void Dispose() { }
    }
    public sealed record AudioVolumeNotificationData(Guid EventContext, bool Muted, float MasterVolume);
    public sealed class AudioEndpointVolume
    {
        private float _scalar = .38f; private bool _mute;
        public Guid NotificationGuid { get; set; }
        public event Action<AudioVolumeNotificationData>? OnVolumeNotification;
        public int Writes { get; private set; }
        public float MasterVolumeLevel => -60 + _scalar * 60;
        public string HardwareSupport => "Synthetic";
        public float MasterVolumeLevelScalar { get => _scalar; set { Writes++; _scalar = value; Notify(NotificationGuid); } }
        public bool Mute { get => _mute; set { Writes++; _mute = value; Notify(NotificationGuid); } }
        public void External(float scalar, bool muted) { _scalar = scalar; _mute = muted; Notify(Guid.Empty); }
        private void Notify(Guid context) => OnVolumeNotification?.Invoke(new(context, _mute, _scalar));
    }
    public sealed class MMDeviceEnumerator : IDisposable
    {
        public MMDevice GetDefaultAudioEndpoint(DataFlow flow, Role role) => new();
        public MMDevice GetDevice(string id) => new();
        public void Dispose() { }
    }
}
namespace NAudio.Wave
{
    public sealed class WaveFormat(int sampleRate, int bitsPerSample, int channels)
    { public int SampleRate => sampleRate; public int BitsPerSample => bitsPerSample; public int Channels => channels; }
    public sealed class WaveInEventArgs(byte[] bytes) : EventArgs { public byte[] Buffer => bytes; public int BytesRecorded => bytes.Length; }
    public sealed class StoppedEventArgs : EventArgs { public Exception? Exception => null; }
    public sealed class WasapiLoopbackCapture : IDisposable
    {
        public static WasapiLoopbackCapture Latest { get; private set; } = null!;
        public WasapiLoopbackCapture(NAudio.CoreAudioApi.MMDevice device) => Latest = this;
        public WaveFormat WaveFormat { get; set; } = new(44100, 16, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new(bytes));
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new());
        public void Dispose() { }
    }
}

namespace WinToPod.Desktop
{
    internal sealed class ProcessAudioCapture : IDisposable
    {
        internal static bool Supported { get; set; } = true;
        internal static Exception? ActivationError { get; set; }
        internal static readonly List<ProcessAudioCapture> Instances = [];
        private readonly Action<byte[], bool> _data;
        private bool _disposed;
        private ProcessAudioCapture(Action<byte[], bool> data) { _data = data; Instances.Add(this); }
        internal static Task<ProcessAudioCapture> OpenAsync(Action<byte[], bool> data, Action<Exception> error, CancellationToken cancellation, uint? includedProcess = null)
        { cancellation.ThrowIfCancellationRequested(); if (ActivationError is { } activationFailure) throw activationFailure; return Task.FromResult(new ProcessAudioCapture(data)); }
        internal void Emit(byte[] bytes, bool gap = false) { if (!_disposed) _data((byte[])bytes.Clone(), gap); }
        public void Dispose() => _disposed = true;
    }
}
