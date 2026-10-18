// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.CoreAudioApi;

namespace WinToPod.Desktop;

// Link the selected Windows endpoint controls to the owned receiver.
// Application-mix capture itself stays independent of the endpoint master gain.
internal sealed class EndpointVolumeLink : IDisposable
{
    private readonly MMDevice _device;
    private readonly Guid _context = Guid.NewGuid();
    private readonly Action<int> _changed;
    private bool _disposed;
    public EndpointVolumeLink(string endpointId, Action<int> changed)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(endpointId); _changed = changed;
        _device.AudioEndpointVolume.NotificationGuid = _context;
        _device.AudioEndpointVolume.OnVolumeNotification += OnNotification;
    }
    private void OnNotification(AudioVolumeNotificationData data)
    {
        if (_disposed || data.EventContext == _context) return;
        _changed(data.Muted ? 0 : Math.Clamp((int)Math.Round(data.MasterVolume * 100), 0, 100));
    }
    public void SetPercent(int percent)
    {
        if (_disposed) return;
        var endpoint = _device.AudioEndpointVolume;
        if (percent == 0) { if (!endpoint.Mute) endpoint.Mute = true; return; }
        var current = endpoint.Mute ? 0 : (int)Math.Round(endpoint.MasterVolumeLevelScalar * 100);
        if (current == percent) return;
        endpoint.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        // A slider change above zero explicitly unmutes, just as the OS controls do.
        if (percent > 0 && endpoint.Mute) endpoint.Mute = false;
    }
    public object? Snapshot()
    {
        if (_disposed) return null;
        try
        {
            var endpoint = _device.AudioEndpointVolume;
            return new { Percent = endpoint.MasterVolumeLevelScalar * 100, Decibels = endpoint.MasterVolumeLevel, endpoint.Mute,
                HardwareSupport = endpoint.HardwareSupport.ToString() };
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ObjectDisposedException) { return new { Unavailable = e.GetType().Name }; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _device.AudioEndpointVolume.OnVolumeNotification -= OnNotification; _device.Dispose();
    }
}
