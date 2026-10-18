// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using WinToPod.AirPlay.Transport;

namespace WinToPod.AirPlay.Pairing;

// Independently implemented HAP setup M1-M6 and verification M1-M4.
// HKP3 normal pairing never enables control encryption after the SRP M4.
// Stored accessory signatures are verified before accepting a channel key.
public static class HapPairingClient
{
    public static async Task<PairingCredentials> SetupAsync(RtspConnection connection, string password, byte[] controllerSeed, CancellationToken cancellation)
    {
        using var srp = new SrpClient();
        var fields = await Exchange(connection, "/pair-setup", Tlv8.Encode((Tlv8.State, [1]), (Tlv8.Method, [0])), 2, cancellation).ConfigureAwait(false);
        var proof = srp.ComputeProof(fields.Require(Tlv8.Salt, 8, 64), fields.Require(Tlv8.PublicKey, 1, 384), password);
        try { fields = await Exchange(connection, "/pair-setup", Tlv8.Encode((Tlv8.State, [3]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, proof)), 4, cancellation).ConfigureAwait(false); }
        catch (PairingRejectedException e) when (e.Code == 2 && e.RetryDelaySeconds is null or 0)
        { throw new PasswordRequiredException(true); }
        var secret = srp.VerifyServer(fields.Require(Tlv8.Proof, 64, 64));
        byte[]? encryptionKey = null, controllerX = null, accessoryX = null, message = null, plain = null;
        try
        {
            encryptionKey = Derive(secret, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
            controllerX = Derive(secret, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
            var id = Encoding.UTF8.GetBytes(connection.PairingClientId);
            var publicKey = new Ed25519PrivateKeyParameters(controllerSeed, 0).GeneratePublicKey().GetEncoded();
            message = Join(controllerX, id, publicKey);
            var signature = Sign(controllerSeed, message);
            var payload = Tlv8.Encode((Tlv8.Identifier, id), (Tlv8.PublicKey, publicKey), (Tlv8.Signature, signature));
            var encrypted = Seal(encryptionKey, "PS-Msg05", payload);
            fields = await Exchange(connection, "/pair-setup", Tlv8.Encode((Tlv8.State, [5]), (Tlv8.EncryptedData, encrypted)), 6, cancellation).ConfigureAwait(false);
            plain = Open(encryptionKey, "PS-Msg06", fields.Require(Tlv8.EncryptedData, 17, 4096));
            var accessory = Tlv8.Decode(plain);
            var accessoryId = accessory.Require(Tlv8.Identifier, 1, 64);
            var accessoryPublic = accessory.Require(Tlv8.PublicKey, 32, 32);
            accessoryX = Derive(secret, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
            Verify(accessoryPublic, Join(accessoryX, accessoryId, accessoryPublic), accessory.Require(Tlv8.Signature, 64, 64));
            return new(connection.PairingClientId, controllerSeed, accessoryId, accessoryPublic);
        }
        finally
        {
            foreach (var bytes in new[] { secret, encryptionKey, controllerX, accessoryX, message, plain }) if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static async Task<byte[]> VerifyAsync(RtspConnection connection, PairingCredentials credentials, CancellationToken cancellation)
    {
        if (connection.PairingClientId != credentials.ControllerId) throw new IOException("已有配对属于另一发送身份，已停止连接，原配对资料保留。");
        var ephemeral = RandomNumberGenerator.GetBytes(32);
        byte[]? shared = null, encryptionKey = null, plain = null, signatureInput = null;
        var completed = false;
        try
        {
            var privateKey = new X25519PrivateKeyParameters(ephemeral, 0);
            var publicKey = privateKey.GeneratePublicKey().GetEncoded();
            var fields = await Exchange(connection, "/pair-verify", Tlv8.Encode((Tlv8.State, [1]), (Tlv8.PublicKey, publicKey)), 2, cancellation).ConfigureAwait(false);
            var accessoryPublic = fields.Require(Tlv8.PublicKey, 32, 32);
            shared = new byte[32];
            try { privateKey.GenerateSecret(new X25519PublicKeyParameters(accessoryPublic, 0), shared, 0); }
            catch (InvalidOperationException e) { throw new CryptographicException("音箱临时公钥无效，已停止连接。", e); }
            if (CryptographicOperations.FixedTimeEquals(shared, new byte[32])) throw new CryptographicException("Invalid X25519 shared secret.");
            encryptionKey = Derive(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
            plain = Open(encryptionKey, "PV-Msg02", fields.Require(Tlv8.EncryptedData, 17, 4096));
            var accessory = Tlv8.Decode(plain);
            var accessoryId = accessory.Require(Tlv8.Identifier, 1, 64);
            if (!CryptographicOperations.FixedTimeEquals(accessoryId, credentials.AccessoryId)) throw new CryptographicException("音箱身份与已保存配对不一致，已停止连接。");
            Verify(credentials.AccessoryPublicKey.ToArray(), Join(accessoryPublic, accessoryId, publicKey), accessory.Require(Tlv8.Signature, 64, 64));
            var controllerId = Encoding.UTF8.GetBytes(credentials.ControllerId);
            signatureInput = Join(publicKey, controllerId, accessoryPublic);
            var signature = Sign(credentials.ControllerSecret, signatureInput);
            var encrypted = Seal(encryptionKey, "PV-Msg03", Tlv8.Encode((Tlv8.Identifier, controllerId), (Tlv8.Signature, signature)));
            await Exchange(connection, "/pair-verify", Tlv8.Encode((Tlv8.State, [3]), (Tlv8.EncryptedData, encrypted)), 4, cancellation).ConfigureAwait(false);
            completed = true; return shared;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeral);
            foreach (var bytes in new[] { encryptionKey, plain, signatureInput }) if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (!completed && shared is not null) CryptographicOperations.ZeroMemory(shared);
        }
    }

    private static async Task<Dictionary<byte, byte[]>> Exchange(RtspConnection connection, string path, byte[] body, byte nextState, CancellationToken cancellation)
    {
        var headers = new Dictionary<string, string> { ["X-Apple-HKP"] = "3", ["X-Apple-Client-Name"] = "WinToPod", ["X-Apple-Client-ID"] = connection.PairingClientId };
        var reply = await connection.RequestAsync("POST", path, body, AirPlayRequestProfile.PairingContentType, headers, cancellation).ConfigureAwait(false);
        reply.RequirePairingSuccess(path == "/pair-verify" ? "配对身份验证" : "完整配对");
        var fields = Tlv8.Decode(reply.Body); fields.RequireState(nextState); return fields;
    }
    private static byte[] Derive(byte[] secret, string salt, string info)
    {
        var key = new byte[32]; HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, key, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info)); return key;
    }
    private static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
    private static byte[] Sign(ReadOnlySpan<byte> seed, byte[] message)
    {
        var copy = seed.ToArray();
        try { var signer = new Ed25519Signer(); signer.Init(true, new Ed25519PrivateKeyParameters(copy, 0)); signer.BlockUpdate(message, 0, message.Length); return signer.GenerateSignature(); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    private static void Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0)); verifier.BlockUpdate(message, 0, message.Length);
        if (!verifier.VerifySignature(signature)) throw new CryptographicException("音箱配对签名验证失败，已停止连接。");
    }
    private static byte[] Seal(byte[] key, string label, byte[] plain)
    {
        var nonce = new byte[12]; Encoding.ASCII.GetBytes(label).CopyTo(nonce, 4); var result = new byte[plain.Length + 16];
        using var cipher = new AeadCipher(key); cipher.Encrypt(nonce, plain, result.AsSpan(0, plain.Length), result.AsSpan(plain.Length), []); return result;
    }
    private static byte[] Open(byte[] key, string label, byte[] encrypted)
    {
        var nonce = new byte[12]; Encoding.ASCII.GetBytes(label).CopyTo(nonce, 4); var result = new byte[encrypted.Length - 16];
        using var cipher = new AeadCipher(key); cipher.Decrypt(nonce, encrypted.AsSpan(0, result.Length), encrypted.AsSpan(result.Length), result, []); return result;
    }
}
