// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;

namespace WinToPod.Desktop;

// Reads only device/header and key-down/button-down presence. Never reads virtual
// key codes, scan codes, text, mouse coordinates, passwords, or clipboard data.
// RDP is explicit; third-party console sharing cannot be identified perfectly.
internal sealed class LocalUseMonitor : IDisposable
{
    private readonly IntPtr _window, _buffer = Marshal.AllocHGlobal(128);
    private readonly Dictionary<IntPtr, bool> _physical = [];
    private bool _locked;
    public bool Registered { get; }
    public int AcceptedIntents { get; private set; }
    public int RejectedIntents { get; private set; }
    public event Action<bool>? Intent;
    public LocalUseMonitor(IntPtr window)
    {
        _window = window;
        var devices = new[] { new RawDevice(1, 6, 0x2100, window), new RawDevice(1, 2, 0x2100, window) };
        Registered = RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawDevice>());
        _ = WTSRegisterSessionNotification(window, 0);
    }
    public bool LocalAndUnlocked
    {
        get
        {
            if (!Registered || _locked || GetSystemMetrics(0x1000) != 0 || GetSystemMetrics(0x2001) != 0) return false;
            if (!Query(16, out var protocol))
                return ProcessIdToSessionId((uint)Environment.ProcessId, out var session) && session == WTSGetActiveConsoleSessionId();
            if (protocol != 0 || !Query(8, out var state)) return false; // WTSConnectState.
            return state == 0; // WTSActive. Unknown session information fails closed.
        }
    }
    private static bool Query(int kind, out int value)
    {
        value = -1;
        if (!WTSQuerySessionInformation(IntPtr.Zero, -1, kind, out var buffer, out var bytes)) return false;
        try { if (bytes < 2) return false; value = kind == 16 ? (ushort)Marshal.ReadInt16(buffer) : bytes >= 4 ? Marshal.ReadInt32(buffer) : -1; return value >= 0; }
        finally { WTSFreeMemory(buffer); }
    }
    public void Process(ref Message message)
    {
        if (message.Msg == 0xFE) { _physical.Clear(); return; }
        if (message.Msg == 0x2B1)
        {
            if ((int)message.WParam is 2 or 3 or 4 or 6 or 7) { _locked = true; Intent?.Invoke(false); }
            else if ((int)message.WParam is 1 or 8) { _locked = false; Intent?.Invoke(false); }
            return;
        }
        if (message.Msg != 0xFF || !Registered) return;
        var headerSize = (uint)Marshal.SizeOf<RawHeader>(); uint size = 128;
        if (GetRawInputData(message.LParam, 0x10000003, _buffer, ref size, headerSize) == uint.MaxValue || size < headerSize + 12) return;
        var header = Marshal.PtrToStructure<RawHeader>(_buffer);
        var isAction = header.Type == 1 ? Marshal.ReadInt32(_buffer, (int)headerSize + 8) is 0x100 or 0x104 :
            header.Type == 0 && ((ushort)Marshal.ReadInt16(_buffer, (int)headerSize + 4) & 0x155) != 0;
        if (!isAction) return;
        if (!_physical.TryGetValue(header.Device, out var physical))
        {
            physical = IsPhysicalDevice(header.Device);
            if (_physical.Count < 64) _physical[header.Device] = physical;
        }
        var accepted = physical && LocalAndUnlocked;
        if (accepted) AcceptedIntents++; else RejectedIntents++;
        Intent?.Invoke(accepted);
    }
    private static bool IsPhysicalDevice(IntPtr device)
    {
        if (device == IntPtr.Zero) return false; // Includes some precision touchpads: no guess.
        uint chars = 0; _ = GetRawInputDeviceInfo(device, 0x20000007, null, ref chars);
        if (chars is 0 or > 2048) return false;
        var name = new StringBuilder((int)chars + 1);
        if (GetRawInputDeviceInfo(device, 0x20000007, name, ref chars) == uint.MaxValue) return false;
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return false;
        try
        {
            var iface = new DeviceInterface { Size = Marshal.SizeOf<DeviceInterface>() };
            if (!SetupDiOpenDeviceInterface(set, name.ToString(), 0, ref iface)) return false;
            var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            _ = SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out _, ref info);
            if (info.DevInst == 0) return false;
            var current = info.DevInst; var hardwareBus = false; var physicalController = false;
            for (var depth = 0; depth < 16; depth++)
            {
                var id = new StringBuilder(512);
                if (CM_Get_Device_ID(current, id, id.Capacity, 0) != 0) return false;
                var value = id.ToString().ToUpperInvariant();
                if (new[] { "RDP", "VIRTUAL", "VHF", "VIGEM", "VMULTI", "PARSEC", "SUNSHINE", "REMOTE", "TO_DESK", "TODESK" }.Any(value.Contains)) return false;
                if (value.StartsWith(@"USB\", StringComparison.Ordinal) || value.StartsWith(@"BTHENUM\", StringComparison.Ordinal) || value.StartsWith(@"BTHLEDEVICE\", StringComparison.Ordinal) ||
                    value.StartsWith(@"ACPI\", StringComparison.Ordinal)) hardwareBus = true;
                if (value.StartsWith(@"PCI\", StringComparison.Ordinal) || value.StartsWith(@"ACPI\", StringComparison.Ordinal)) physicalController = true;
                if (CM_Get_Parent(out var parent, current, 0) != 0) return hardwareBus && physicalController;
                current = parent;
            }
            return false;
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }
    public void Dispose()
    {
        _ = WTSUnRegisterSessionNotification(_window);
        if (Registered) _ = RegisterRawInputDevices([new(1, 6, 1, IntPtr.Zero), new(1, 2, 1, IntPtr.Zero)], 2, (uint)Marshal.SizeOf<RawDevice>());
        Marshal.FreeHGlobal(_buffer);
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct RawDevice(ushort Page, ushort Usage, uint Flags, IntPtr Target);
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { public int Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int kind, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("setupapi.dll")] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref DeviceInterface data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface data, IntPtr detail, uint size, out uint needed, ref DeviceInfo info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID(uint devInst, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
}
