// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;

namespace WinToPod.AirPlay;

// Crypto primitive only, not an AirPlay engine. Managed fallback supports Windows 10
// releases whose CNG does not expose ChaCha20-Poly1305.
public sealed class AeadCipher : IDisposable
{
    private readonly ChaCha20Poly1305? _native;
    private readonly byte[] _key;
    private bool _disposed;
    public AeadCipher(ReadOnlySpan<byte> key, bool managedOnly = false)
    {
        if (key.Length != 32) throw new ArgumentException("A 32-byte key is required.");
        _key = key.ToArray();
        if (!managedOnly && ChaCha20Poly1305.IsSupported) _native = new(_key);
    }
    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag, ReadOnlySpan<byte> aad)
    {
        Validate(nonce, plain.Length, cipher.Length, tag.Length);
        if (_native is not null) { _native.Encrypt(nonce, plain, cipher, tag, aad); return; }
        var result = Transform(true, nonce, plain, aad); result.AsSpan(0, plain.Length).CopyTo(cipher); result.AsSpan(plain.Length).CopyTo(tag);
        CryptographicOperations.ZeroMemory(result);
    }
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain, ReadOnlySpan<byte> aad)
    {
        Validate(nonce, cipher.Length, plain.Length, tag.Length);
        if (_native is not null) { _native.Decrypt(nonce, cipher, tag, plain, aad); return; }
        var combined = new byte[cipher.Length + 16]; cipher.CopyTo(combined); tag.CopyTo(combined.AsSpan(cipher.Length));
        byte[]? result = null;
        try { result = Transform(false, nonce, combined, aad); result.CopyTo(plain); }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException e) { plain.Clear(); throw new CryptographicException("Invalid authentication tag.", e); }
        finally { CryptographicOperations.ZeroMemory(combined); if (result is not null) CryptographicOperations.ZeroMemory(result); }
    }
    private byte[] Transform(bool encrypt, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, ReadOnlySpan<byte> aad)
    {
        var primitive = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        primitive.Init(encrypt, new AeadParameters(new KeyParameter(_key), 128, nonce.ToArray(), aad.ToArray()));
        var source = input.ToArray(); var output = new byte[primitive.GetOutputSize(source.Length)];
        try { var count = primitive.ProcessBytes(source, 0, source.Length, output, 0); primitive.DoFinal(output, count); return output; }
        catch { CryptographicOperations.ZeroMemory(output); throw; }
        finally { CryptographicOperations.ZeroMemory(source); }
    }
    private void Validate(ReadOnlySpan<byte> nonce, int source, int destination, int tag)
    { ObjectDisposedException.ThrowIf(_disposed, this); if (nonce.Length != 12 || source != destination || tag != 16) throw new ArgumentException("Invalid AEAD sizes."); }
    public void Dispose() { if (_disposed) return; _disposed = true; _native?.Dispose(); CryptographicOperations.ZeroMemory(_key); }
}
