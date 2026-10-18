// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinToPod.AirPlay;

// One object per traffic direction, owned by one protocol I/O loop. This class is not thread-safe.
// No Apple identity or pairing is implied: the authenticated pairing layer supplies the authenticated key.
public sealed class ControlFrameCipher : IDisposable
{
    private readonly AeadCipher _cipher;
    private readonly int _maxPayload;
    private ulong _counter;
    private bool _failed, _disposed;

    public ControlFrameCipher(ReadOnlySpan<byte> key, int maxPayload = 1024)
    {
        if (key.Length != 32) throw new ArgumentException("A 32-byte session key is required.", nameof(key));
        if (maxPayload is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxPayload));
        _cipher = new(key); _maxPayload = maxPayload;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        EnsureUsable();
        if (plaintext.Length > _maxPayload) throw new ArgumentOutOfRangeException(nameof(plaintext));
        var frame = new byte[2 + plaintext.Length + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)plaintext.Length);
        Span<byte> nonce = stackalloc byte[12]; nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _counter);
        _cipher.Encrypt(nonce, plaintext, frame.AsSpan(2, plaintext.Length), frame.AsSpan(2 + plaintext.Length, 16), frame.AsSpan(0, 2));
        Advance();
        return frame;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> frame)
    {
        EnsureUsable();
        try
        {
            if (frame.Length < 18) throw new InvalidDataException("Incomplete control frame.");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frame);
            if (length > _maxPayload || frame.Length != 2 + length + 16)
                throw new InvalidDataException("Invalid control-frame length.");
            var plaintext = new byte[length];
            Span<byte> nonce = stackalloc byte[12]; nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _counter);
            try { _cipher.Decrypt(nonce, frame.Slice(2, length), frame.Slice(2 + length, 16), plaintext, frame[..2]); }
            catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
            Advance();
            return plaintext;
        }
        catch { _failed = true; throw; } // Framing/auth failure terminates this direction; never guess the next nonce.
    }

    public static byte[] DeriveControlKey(ReadOnlySpan<byte> authenticatedSecret, bool clientWrites)
    {
        if (authenticatedSecret.IsEmpty) throw new ArgumentException("Authenticated secret is required.", nameof(authenticatedSecret));
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, authenticatedSecret, key,
            Encoding.ASCII.GetBytes("Control-Salt"),
            Encoding.ASCII.GetBytes(clientWrites ? "Control-Write-Encryption-Key" : "Control-Read-Encryption-Key"));
        return key;
    }

    private void Advance()
    {
        if (_counter == ulong.MaxValue) _failed = true; // Last unique nonce is consumed; no wrap-around.
        else ++_counter;
    }
    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidOperationException("This control direction is closed.");
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _cipher.Dispose(); }
}
