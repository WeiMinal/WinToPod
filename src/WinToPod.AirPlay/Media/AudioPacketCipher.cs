// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinToPod.AirPlay.Media;

public sealed class AudioPacketCipher(byte[] key, ulong firstNonce = 0) : IDisposable
{
    private readonly AeadCipher _cipher = new(key);
    private ulong _nonce = firstNonce; private bool _exhausted;
    public byte[] Encrypt(byte[] frame, ushort sequence, uint timestamp, uint ssrc, bool first)
    {
        if (_exhausted) throw new InvalidOperationException("Audio nonce exhausted.");
        if (frame.Length is < 1 or > 2048) throw new ArgumentException("Invalid audio frame size.");
        var packet = new byte[12 + frame.Length + 24]; packet[0] = 0x80; packet[1] = first ? (byte)0xe0 : (byte)0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp); BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        Span<byte> nonce = stackalloc byte[12]; nonce.Clear(); BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _nonce);
        _cipher.Encrypt(nonce, frame, packet.AsSpan(12, frame.Length), packet.AsSpan(12 + frame.Length, 16), packet.AsSpan(4, 8));
        nonce[4..].CopyTo(packet.AsSpan(packet.Length - 8));
        if (_nonce == ulong.MaxValue) _exhausted = true; else _nonce++;
        return packet;
    }
    public void Dispose() { _exhausted = true; _cipher.Dispose(); }
}
