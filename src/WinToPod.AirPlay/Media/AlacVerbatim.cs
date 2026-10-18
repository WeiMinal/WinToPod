// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinToPod.AirPlay.Media;

// ALAC stereo 16-bit escape frame, not LPCM mislabeled as ALAC.
// Explicit partial-frame length and END element; no predictor/compression code.
public static class AlacVerbatim
{
    public static byte[] Encode(ReadOnlySpan<byte> pcmLittleEndian)
    {
        if (pcmLittleEndian.Length == 0 || pcmLittleEndian.Length % 4 != 0 || pcmLittleEndian.Length > 352 * 4)
            throw new ArgumentException("Expected 1–352 stereo S16LE frames.");
        var samples = pcmLittleEndian.Length / 4;
        var bytes = new byte[(23 + 32 + samples * 32 + 3 + 7) / 8]; var bit = 0;
        void Put(uint value, int width)
        { for (var i = width - 1; i >= 0; i--, bit++) if (((value >> i) & 1) != 0) bytes[bit / 8] |= (byte)(1 << (7 - bit % 8)); }
        Put(1, 3); Put(0, 4); Put(0, 12); Put(1, 1); Put(0, 2); Put(1, 1); Put((uint)samples, 32);
        for (var at = 0; at < pcmLittleEndian.Length; at += 2) Put(BinaryPrimitives.ReadUInt16LittleEndian(pcmLittleEndian[at..]), 16);
        Put(7, 3); return bytes;
    }
}
