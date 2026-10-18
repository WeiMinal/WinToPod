// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace WinToPod.Desktop;

// Native Windows process-loopback activation. Captures the application mix
// independently of the physical endpoint's master volume. Never a microphone.
// All COM operations and teardown stay on one MTA thread; no UI-thread waits.
// Public holder keeps its classic-COM callback interfaces visible to native QI.
public sealed class ProcessAudioCapture : IDisposable
{
    private readonly ManualResetEvent _stop = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Action<byte[], bool> _data;
    private readonly Action<Exception> _error;
    private readonly uint? _includedProcess;
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    private int _disposed;
    private ProcessAudioCapture(Action<byte[], bool> data, Action<Exception> error, uint? includedProcess)
    {
        _data = data; _error = error; _includedProcess = includedProcess;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinToPod application audio" };
        _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
    }
    public static async Task<ProcessAudioCapture> OpenAsync(Action<byte[], bool> data, Action<Exception> error, CancellationToken cancellation, uint? includedProcess = null)
    {
        var capture = new ProcessAudioCapture(data, error, includedProcess);
        try { await capture._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation); return capture; }
        catch { capture.Dispose(); throw; }
    }
    private void Run()
    {
        IActivationOperation? operation = null;
        try
        {
            var completion = new ActivationCompletion();
            var args = new ActivationParameters { Type = 1, ProcessId = _includedProcess ?? (uint)Environment.ProcessId, Mode = _includedProcess is null ? 1 : 0 };
            var blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
            try
            {
                Marshal.StructureToPtr(args, blob, false);
                var variant = new BlobVariant { VariantType = 65, Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = blob };
                var iid = typeof(IAudioClient).GUID;
                Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", ref iid, ref variant, completion, out operation));
                // Windows owns the callback until completion. The params and
                // operation also outlive it, including cancellation/slow startup.
                completion.Done.WaitOne();
                if (completion.Error is { } failure) throw failure;
                using var client = new AudioClient((IAudioClient)(completion.Client ?? throw new IOException("Windows音频接口为空。")));
                if (_stop.WaitOne(0)) return;
                using var samples = new AutoResetEvent(false);
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                    0, 0, new WaveFormat(44100, 16, 2), Guid.Empty);
                client.SetEventHandle(samples.SafeWaitHandle.DangerousGetHandle());
                var input = client.AudioCaptureClient;
                client.Start(); _ready.TrySetResult();
                try
                {
                    var waits = new WaitHandle[] { _stop, samples };
                    while (WaitHandle.WaitAny(waits, 500) != 0)
                    {
                        while (!_stop.WaitOne(0) && input.GetNextPacketSize() > 0)
                        {
                            var pointer = input.GetBuffer(out var frames, out var flags);
                            try
                            {
                                var copy = new byte[checked(frames * 4)];
                                var silent = (flags & AudioClientBufferFlags.Silent) != 0;
                                if (!silent && copy.Length > 0) Marshal.Copy(pointer, copy, 0, copy.Length);
                                _data(copy, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                            }
                            finally { input.ReleaseBuffer(frames); }
                        }
                    }
                }
                finally { client.Stop(); }
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
                if (operation is not null && Marshal.IsComObject(operation)) Marshal.ReleaseComObject(operation);
                completion.Done.Dispose();
            }
        }
        catch (Exception e) { _ready.TrySetException(e); if (Volatile.Read(ref _disposed) == 0) _error(e); }
        finally { _ready.TrySetException(new IOException("Windows音频采集已停止。")); _stop.Dispose(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Set(); } catch (ObjectDisposedException) { }
        // The MTA thread releases its own COM references, including a pending
        // activation. Do not block the UI or release a live callback early.
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ActivationParameters { public int Type; public uint ProcessId; public int Mode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct BlobVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }
    [ComImport, ComVisible(true), Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivationOperation
    {
        [PreserveSig] int GetActivateResult(out int activationResult, [MarshalAs(UnmanagedType.IUnknown)] out object client);
    }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivationCompletion
    { [PreserveSig] int ActivateCompleted(IActivationOperation operation); }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileCallback { }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationCompletion : IActivationCompletion, IAgileCallback
    {
        internal readonly ManualResetEvent Done = new(false);
        internal object? Client;
        internal Exception? Error;
        public int ActivateCompleted(IActivationOperation operation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var hr, out var client));
                Marshal.ThrowExceptionForHR(hr); Client = client;
            }
            catch (Exception e) { Error = e; }
            finally { Done.Set(); }
            return 0;
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, ref BlobVariant parameters,
        [MarshalAs(UnmanagedType.Interface)] IActivationCompletion completion, out IActivationOperation operation);
}
