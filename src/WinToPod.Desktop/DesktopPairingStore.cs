// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinToPod.AirPlay.Pairing;

namespace WinToPod.Desktop;

// Only cipher text is written, including temporary files. The Home password is
// never stored here. A single controller seed is shared across all receivers.
internal sealed class DesktopPairingStore(string? directory = null) : IReceiverPairingStore, IDisposable
{
    private readonly string _path = Path.Combine(directory ?? DesktopSettings.DirectoryPath, "paired-receivers.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinToPod/paired-receivers/current-user");
    private readonly object _gate = new();
    private State? _state;
    private bool _disposed;
    private sealed class Peer
    {
        public Peer() { }
        public byte[] Id { get; set; } = [];
        public byte[] PublicKey { get; set; } = [];
    }
    private sealed class State
    {
        public State() { }
        public int Schema { get; set; } = 1;
        public string ControllerId { get; set; } = "";
        public byte[] ControllerSecret { get; set; } = [];
        public Dictionary<string, Peer> Receivers { get; set; } = new(StringComparer.Ordinal);
    }
    public PairingCredentials? Load(string receiverId, string controllerId)
    {
        lock (_gate)
        {
            var state = Read(controllerId);
            return state?.Receivers.TryGetValue(receiverId, out var peer) == true ? new(controllerId, state.ControllerSecret, peer.Id, peer.PublicKey) : null;
        }
    }
    public byte[] GetControllerSecret(string controllerId)
    {
        lock (_gate)
        {
            var state = Read(controllerId);
            if (state is null)
            {
                state = new() { ControllerId = controllerId, ControllerSecret = RandomNumberGenerator.GetBytes(32) };
                try { Write(state); _state = state; }
                catch { CryptographicOperations.ZeroMemory(state.ControllerSecret); throw; }
            }
            return (byte[])state.ControllerSecret.Clone();
        }
    }
    public void Save(string receiverId, PairingCredentials credentials)
    {
        lock (_gate)
        {
            var state = Read(credentials.ControllerId) ?? throw new IOException("尚未保存发送端配对身份，已停止。");
            if (string.IsNullOrWhiteSpace(receiverId) || state.Receivers.Count >= 1024 || state.Receivers.ContainsKey(receiverId) ||
                !CryptographicOperations.FixedTimeEquals(state.ControllerSecret, credentials.ControllerSecret))
                throw new IOException("配对资料不一致或已有记录；不会自动覆盖原配对。");
            state.Receivers.Add(receiverId, new() { Id = credentials.AccessoryId.ToArray(), PublicKey = credentials.AccessoryPublicKey.ToArray() });
            try { Write(state); }
            catch { state.Receivers.Remove(receiverId); throw; }
        }
    }
    private State? Read(string controllerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state is not null) { CheckController(_state, controllerId); return _state; }
        if (!File.Exists(_path)) return null;
        byte[]? plain = null;
        try
        {
            if (new FileInfo(_path).Length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException();
            plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length is < 34 or > 1024 * 1024 || !CryptographicOperations.FixedTimeEquals(plain.AsSpan(0, 32), SHA256.HashData(plain.AsSpan(32)))) throw new InvalidDataException();
            var state = JsonSerializer.Deserialize<State>(plain.AsSpan(32)) ?? throw new InvalidDataException();
            if (state.Schema != 1 || state.ControllerSecret is not { Length: 32 } || !Guid.TryParse(state.ControllerId, out _) || state.Receivers is null ||
                state.Receivers.Count > 1024 || state.Receivers.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Id is null || p.Value.Id.Length is < 1 or > 64 || p.Value.PublicKey is not { Length: 32 }))
                throw new InvalidDataException();
            CheckController(state, controllerId); _state = state; return state;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        { throw new IOException("配对资料无法解密或格式不兼容，已停止连接。原文件保留，不自动删除或重新配对。", e); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    private static void CheckController(State state, string controllerId)
    { if (state.ControllerId != controllerId) throw new IOException("当前发送身份与已有配对不一致，原配对资料已保留，已停止连接。"); }
    private void Write(State state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state); var plain = new byte[32 + json.Length]; byte[]? encrypted = null;
        try
        {
            SHA256.HashData(json).CopyTo(plain, 0); json.CopyTo(plain, 32);
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(encrypted); file.Flush(true); }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(json); CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; if (_state is not null) CryptographicOperations.ZeroMemory(_state.ControllerSecret); _state = null; }
    }
}
