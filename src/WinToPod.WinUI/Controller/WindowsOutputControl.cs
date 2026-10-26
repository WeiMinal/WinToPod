// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
namespace WinToPod.Desktop;

// Optional, isolated undocumented PolicyConfig boundary; validate and read back
// every write. UI also offers the official Windows sound settings fallback.
internal static class WindowsOutputControl
{
    public static IReadOnlyList<CaptureChoice> List()
    {
        var result = new List<CaptureChoice>(); using var e = new MMDeviceEnumerator();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (d) result.Add(new(d.FriendlyName, d.ID));
        return result;
    }
    public static string Current()
    {
        using var e = new MMDeviceEnumerator(); using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); return d.ID;
    }
    public static void SetDefault(string id)
    {
        if (!List().Any(d => d.Id == id)) throw new IOException("所选输出设备已不可用，请重新打开列表。");
        var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        try
        {
            var policy = (IPolicyConfig)instance; using var e = new MMDeviceEnumerator();
            OutputSwitchTransaction.Apply(id, role => { using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, (Role)role); return d.ID; },
                (role, value) => Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(value, role)));
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        { throw new IOException("Windows 未能切换默认输出，请通过“Windows 声音设置”切换后重试。", e); }
        finally { if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
    }
    // ABI placeholders preserve slots 3..12 and are never invoked. The used
    // SetDefaultEndpoint method is slot 13, with an UTF-16 endpoint ID.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
