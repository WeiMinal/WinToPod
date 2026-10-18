// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinToPod.AirPlay.Transport;

// One read loop and one write loop; no concurrent reads or concurrent writes.
public sealed class EncryptedControlStream : Stream
{
    private readonly Stream _inner;
    private readonly ControlFrameCipher _read, _write;
    private byte[] _pending = []; private int _offset; private bool _failed;
    public EncryptedControlStream(Stream inner, byte[] authenticatedSecret)
        : this(inner, authenticatedSecret, false) { }
    public EncryptedControlStream(Stream inner, byte[] authenticatedSecret, bool eventChannel)
    {
        _inner = inner;
        byte[] Derive(bool writes)
        {
            if (!eventChannel) return ControlFrameCipher.DeriveControlKey(authenticatedSecret, writes);
            var key = new byte[32];
            // Reverse event connection: receiver's Write is our Read.
            HKDF.DeriveKey(HashAlgorithmName.SHA512, authenticatedSecret, key,
                "Events-Salt"u8, writes ? "Events-Read-Encryption-Key"u8 : "Events-Write-Encryption-Key"u8);
            return key;
        }
        var read = Derive(false); var write = Derive(true);
        try { _read = new(read); _write = new(write); }
        finally { CryptographicOperations.ZeroMemory(read); CryptographicOperations.ZeroMemory(write); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_failed) throw new IOException("Encrypted control stream is closed.");
        if (buffer.Length == 0) return 0;
        try
        {
            while (_offset == _pending.Length)
            {
                var header = new byte[2]; var first = await _inner.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (first == 0) return 0;
                await _inner.ReadExactlyAsync(header.AsMemory(1, 1), cancellationToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
                if (length > 1024) throw new InvalidDataException("Encrypted control frame exceeds 1024 bytes.");
                var frame = new byte[2 + length + 16]; header.CopyTo(frame, 0);
                await _inner.ReadExactlyAsync(frame.AsMemory(2), cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(_pending); _pending = _read.Decrypt(frame); _offset = 0;
            }
            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        catch { _failed = true; throw; }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_failed) throw new IOException("Encrypted control stream is closed.");
        try
        {
            for (var at = 0; at < buffer.Length; at += 1024)
                await _inner.WriteAsync(_write.Encrypt(buffer.Span.Slice(at, Math.Min(1024, buffer.Length - at))), cancellationToken).ConfigureAwait(false);
        }
        catch { _failed = true; throw; }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { _failed = true; _read.Dispose(); _write.Dispose(); CryptographicOperations.ZeroMemory(_pending); } base.Dispose(disposing); }
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
