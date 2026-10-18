// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace WinToPod.AirPlay.Pairing;

// Secret-bearing type: never use a positional record or print its contents.
public sealed class PairingCredentials : IDisposable
{
    private readonly byte[] _controllerSecret, _accessoryId, _accessoryPublicKey;
    private bool _disposed;
    public string ControllerId { get; }
    public ReadOnlySpan<byte> ControllerSecret { get { ThrowIfDisposed(); return _controllerSecret; } }
    public ReadOnlySpan<byte> AccessoryId { get { ThrowIfDisposed(); return _accessoryId; } }
    public ReadOnlySpan<byte> AccessoryPublicKey { get { ThrowIfDisposed(); return _accessoryPublicKey; } }
    public PairingCredentials(string controllerId, ReadOnlySpan<byte> controllerSecret, ReadOnlySpan<byte> accessoryId, ReadOnlySpan<byte> accessoryPublicKey)
    {
        if (!Guid.TryParse(controllerId, out _) || controllerSecret.Length != 32 || accessoryId.Length is < 1 or > 64 || accessoryPublicKey.Length != 32)
            throw new InvalidDataException("Invalid pairing identity.");
        ControllerId = controllerId; _controllerSecret = controllerSecret.ToArray();
        _accessoryId = accessoryId.ToArray(); _accessoryPublicKey = accessoryPublicKey.ToArray();
    }
    public PairingCredentials Copy() => new(ControllerId, ControllerSecret, AccessoryId, AccessoryPublicKey);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override string ToString() => "WinToPod pairing credentials (redacted)";
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_controllerSecret); }
}

public interface IReceiverPairingStore
{
    PairingCredentials? Load(string receiverId, string controllerId);
    byte[] GetControllerSecret(string controllerId);
    void Save(string receiverId, PairingCredentials credentials);
}

// Used by independent checks and callers that explicitly omit a persistent store.
// All members share ONE controller key for a given ID, even before any M6 reply.
public sealed class MemoryReceiverPairingStore : IReceiverPairingStore, IDisposable
{
    private readonly Dictionary<string, byte[]> _controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PairingCredentials> _receivers = new(StringComparer.Ordinal);
    private bool _disposed;
    public int SavedCount => _receivers.Count;
    public PairingCredentials? Load(string receiverId, string controllerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_receivers.TryGetValue(receiverId, out var value)) return null;
        if (value.ControllerId != controllerId) throw new IOException("配对身份不匹配，已停止连接。");
        return value.Copy();
    }
    public byte[] GetControllerSecret(string controllerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_controllers.TryGetValue(controllerId, out var secret)) _controllers[controllerId] = secret = RandomNumberGenerator.GetBytes(32);
        return (byte[])secret.Clone();
    }
    public void Save(string receiverId, PairingCredentials credentials)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receivers.ContainsKey(receiverId)) throw new IOException("已有配对不会被自动覆盖。");
        _receivers.Add(receiverId, credentials.Copy());
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var secret in _controllers.Values) CryptographicOperations.ZeroMemory(secret);
        foreach (var receiver in _receivers.Values) receiver.Dispose();
        _controllers.Clear(); _receivers.Clear();
    }
}
