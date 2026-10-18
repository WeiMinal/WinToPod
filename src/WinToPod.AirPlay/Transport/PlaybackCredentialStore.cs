// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace WinToPod.AirPlay.Transport;

// A realm-specific Digest base HA1 is authentication material, not a password.
// It must receive the same protection as a password and stay receiver-scoped.
public sealed class PlaybackCredential : IDisposable
{
    public string Scope { get; }
    public string Algorithm { get; }
    private readonly byte[] _secret;
    private bool _disposed;
    public PlaybackCredential(string scope, string algorithm, ReadOnlySpan<byte> secret)
    {
        if (scope is null || scope.Length != 64 || scope.Any(c => !char.IsAsciiHexDigit(c)) ||
            algorithm is not ("MD5" or "SHA-256") || secret.Length != (algorithm == "MD5" ? 16 : 32)) throw new ArgumentException("Invalid playback credential.");
        Scope = scope; Algorithm = algorithm; _secret = secret.ToArray();
    }
    public byte[] CopySecret() { ObjectDisposedException.ThrowIf(_disposed, this); return (byte[])_secret.Clone(); }
    public PlaybackCredential Copy() { ObjectDisposedException.ThrowIf(_disposed, this); return new(Scope, Algorithm, _secret); }
    public override string ToString() => "PlaybackCredential [redacted]";
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_secret); }
}

public interface IPlaybackCredentialStore
{
    PlaybackCredential? Load(string receiverId, string scope);
    void Save(string receiverId, PlaybackCredential credential);
    void Forget(string receiverId, string? scope = null);
}

public sealed class MemoryPlaybackCredentialStore : IPlaybackCredentialStore, IDisposable
{
    private readonly Dictionary<(string Receiver, string Scope), PlaybackCredential> _values = [];
    public PlaybackCredential? Load(string receiverId, string scope)
    { lock (_values) return _values.TryGetValue((receiverId, scope), out var credential) ? credential.Copy() : null; }
    public void Save(string receiverId, PlaybackCredential credential)
    {
        lock (_values)
        {
            var key = (receiverId, credential.Scope);
            if (_values.Remove(key, out var old)) old.Dispose();
            _values.Add(key, credential.Copy());
        }
    }
    public void Forget(string receiverId, string? scope = null)
    { lock (_values) foreach (var key in _values.Keys.Where(k => k.Receiver == receiverId && (scope is null || k.Scope == scope)).ToArray()) { _values[key].Dispose(); _values.Remove(key); } }
    public void Dispose() { lock (_values) { foreach (var value in _values.Values) value.Dispose(); _values.Clear(); } }
}
