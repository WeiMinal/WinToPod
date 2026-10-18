// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using WinToPod.AirPlay.Transport;

namespace WinToPod.Desktop;

// Separate from pairing identity. Only receiver/realm-specific Digest material
// is retained. No password, plaintext temp file, or cross-receiver fallback.
internal sealed class DesktopPlaybackCredentialStore : IPlaybackCredentialStore, IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryPlaybackCredentialStore _memory = new();
    private readonly Dictionary<string, bool> _remember = new(StringComparer.Ordinal);
    private readonly string _path;
    private static readonly byte[] Entropy = "WinToPod playback credentials v1"u8.ToArray();
    private State? _state;
    private bool _loaded, _disposed;
    public DesktopPlaybackCredentialStore(string? directory = null) => _path = Path.Combine(directory ?? DesktopSettings.DirectoryPath, "playback-credentials.dat");
    private sealed class Entry
    {
        public Entry() { }
        public string Receiver { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Algorithm { get; set; } = "";
        public byte[] Secret { get; set; } = [];
    }
    private sealed class State
    {
        public State() { }
        public int Schema { get; set; } = 1;
        public List<Entry> Entries { get; set; } = [];
    }
    public void SetRemember(string receiverId, bool remember)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _remember[receiverId] = remember; } }
    public PlaybackCredential? Load(string receiverId, string scope)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_memory.Load(receiverId, scope) is { } memory) return memory;
            var entry = Read().Entries.SingleOrDefault(x => x.Receiver == receiverId && x.Scope == scope);
            return entry is null ? null : new(entry.Scope, entry.Algorithm, entry.Secret);
        }
    }
    public void Save(string receiverId, PlaybackCredential credential)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _memory.Save(receiverId, credential);
            if (!_remember.GetValueOrDefault(receiverId)) return;
            var state = Read(); var old = state.Entries.SingleOrDefault(x => x.Receiver == receiverId && x.Scope == credential.Scope);
            if (old is null && state.Entries.Count >= 1024) throw new IOException("已记住的音箱播放凭据达到上限。");
            var entry = new Entry { Receiver = receiverId, Scope = credential.Scope, Algorithm = credential.Algorithm, Secret = credential.CopySecret() };
            if (old is not null) state.Entries.Remove(old); state.Entries.Add(entry);
            try { Write(state); if (old is not null) CryptographicOperations.ZeroMemory(old.Secret); }
            catch { state.Entries.Remove(entry); CryptographicOperations.ZeroMemory(entry.Secret); if (old is not null) state.Entries.Add(old); throw; }
        }
    }
    public void Forget(string receiverId, string? scope = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _memory.Forget(receiverId, scope);
            var state = Read(); var removed = state.Entries.Where(x => x.Receiver == receiverId && (scope is null || x.Scope == scope)).ToArray();
            if (removed.Length == 0) return;
            foreach (var entry in removed) state.Entries.Remove(entry);
            try { Write(state); foreach (var entry in removed) CryptographicOperations.ZeroMemory(entry.Secret); }
            catch { state.Entries.AddRange(removed); throw; }
        }
    }
    private State Read()
    {
        if (_loaded) return _state!;
        if (!File.Exists(_path)) { _loaded = true; return _state = new(); }
        byte[]? plain = null;
        try
        {
            if (new FileInfo(_path).Length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException();
            plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length is < 34 or > 1024 * 1024 || !CryptographicOperations.FixedTimeEquals(plain.AsSpan(0, 32), SHA256.HashData(plain.AsSpan(32)))) throw new InvalidDataException();
            var state = JsonSerializer.Deserialize<State>(plain.AsSpan(32)) ?? throw new InvalidDataException();
            if (state.Schema != 1 || state.Entries is null || state.Entries.Count > 1024 || state.Entries.Any(x => x is null || string.IsNullOrWhiteSpace(x.Receiver) || x.Receiver.Length > 256) ||
                state.Entries.Select(x => (x.Receiver, x.Scope)).Distinct().Count() != state.Entries.Count) throw new InvalidDataException();
            foreach (var entry in state.Entries) { using var check = new PlaybackCredential(entry.Scope, entry.Algorithm, entry.Secret); }
            _state = state; _loaded = true; return state;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidDataException or ArgumentException)
        { throw new IOException("记住的播放凭据无法解密或格式异常，原文件已保留；配对身份不受影响。", error); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    private void Write(State state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state); var plain = new byte[32 + json.Length];
        try
        {
            SHA256.HashData(json).CopyTo(plain, 0); json.CopyTo(plain, 32);
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(encrypted); file.Flush(true); }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(json); CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; _memory.Dispose(); if (_state is not null) foreach (var entry in _state.Entries) CryptographicOperations.ZeroMemory(entry.Secret); _state = null; _remember.Clear(); } }
}
