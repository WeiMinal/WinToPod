// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WinToPod.AirPlay.Pairing;

// SRP-6a, RFC5054 3072-bit group, SHA-512, HAP identity Pair-Setup.
// The deterministic private value overload is for independent fixed-answer checks.
public sealed class SrpClient : IDisposable
{
    public const string ModulusHex = "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";
    private static readonly BigInteger N = Number(Convert.FromHexString(ModulusHex));
    private static readonly BigInteger G = 5;
    private readonly BigInteger _a;
    private readonly byte[] _public;
    private byte[]? _key, _proof, _serverProof;
    private bool _disposed;
    public byte[] PublicKey => (byte[])_public.Clone();

    public SrpClient(byte[]? privateValue = null)
    {
        var secret = privateValue is null ? RandomNumberGenerator.GetBytes(32) : (byte[])privateValue.Clone();
        try
        {
            if (secret.Length < 32 || secret.Length > 64) throw new ArgumentException("SRP private value must be 32–64 bytes.");
            _a = Number(secret); if (_a == 0) throw new ArgumentException("Zero SRP private value.");
            _public = BigInteger.ModPow(G, _a, N).ToByteArray(true, true);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public byte[] ComputeProof(byte[] salt, byte[] serverPublic, string password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_key is not null) throw new InvalidOperationException("One SRP challenge per client.");
        if (salt.Length is < 8 or > 64 || serverPublic.Length is < 1 or > 384 || password.Length is < 1 or > 1024)
            throw new InvalidDataException("Invalid SRP challenge.");
        var b = Number(serverPublic);
        if (b <= 0 || b >= N || b % N == 0) throw new CryptographicException("Invalid SRP public value.");
        var paddedB = Pad(b);
        var k = Number(Hash(Pad(N), Pad(G))); var u = Number(Hash(Pad(Number(_public)), paddedB));
        if (u == 0) throw new CryptographicException("Invalid SRP scrambling parameter.");
        var credential = Encoding.UTF8.GetBytes("Pair-Setup:" + password);
        byte[] inner;
        try { inner = SHA512.HashData(credential); }
        finally { CryptographicOperations.ZeroMemory(credential); }
        var xBytes = Hash(salt, inner); CryptographicOperations.ZeroMemory(inner);
        var x = Number(xBytes); CryptographicOperations.ZeroMemory(xBytes);
        var baseValue = (b - k * BigInteger.ModPow(G, x, N)) % N; if (baseValue < 0) baseValue += N;
        if (baseValue == 0) throw new CryptographicException("Invalid SRP premaster base.");
        var s = BigInteger.ModPow(baseValue, _a + u * x, N);
        var sBytes = s.ToByteArray(true, true);
        try { _key = SHA512.HashData(sBytes); }
        finally { CryptographicOperations.ZeroMemory(sBytes); }
        var hN = SHA512.HashData(N.ToByteArray(true, true)); var hG = SHA512.HashData(G.ToByteArray(true, true));
        for (var i = 0; i < hN.Length; i++) hN[i] ^= hG[i];
        _proof = Hash(hN, SHA512.HashData(Encoding.UTF8.GetBytes("Pair-Setup")), salt, _public, b.ToByteArray(true, true), _key);
        _serverProof = Hash(_public, _proof, _key);
        return (byte[])_proof.Clone();
    }

    public byte[] VerifyServer(byte[] proof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_serverProof is null || _key is null) throw new InvalidOperationException("SRP challenge not computed.");
        if (!CryptographicOperations.FixedTimeEquals(proof, _serverProof))
        { Dispose(); throw new CryptographicException("音箱SRP证明校验失败，已终止连接。"); }
        return (byte[])_key.Clone();
    }

    private static BigInteger Number(byte[] bytes) => new(bytes, true, true);
    private static byte[] Pad(BigInteger value)
    {
        var raw = value.ToByteArray(true, true); var result = new byte[384]; raw.CopyTo(result, 384 - raw.Length); return result;
    }
    private static byte[] Hash(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) hash.AppendData(p); return hash.GetHashAndReset();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var value in new[] { _key, _proof, _serverProof }) if (value is not null) CryptographicOperations.ZeroMemory(value);
        // BigInteger is immutable; this managed implementation cannot guarantee scrubbing its limbs.
    }
}
