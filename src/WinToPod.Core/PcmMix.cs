// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinToPod.Core;

public static class PcmMix
{
    // Preserve content from both stereo channels when only one native-pair
    // member is used. Send identical mono in both channels; no L/R guessing.
    public static void ToDualMono(Span<byte> pcm)
    {
        if (pcm.Length % 4 != 0) throw new ArgumentException("Expected interleaved stereo s16le frames.", nameof(pcm));
        for (var at = 0; at < pcm.Length; at += 4)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(pcm[at..]);
            var right = BinaryPrimitives.ReadInt16LittleEndian(pcm[(at + 2)..]);
            var mono = (short)(((int)left + right) / 2);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[at..], mono);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[(at + 2)..], mono);
        }
    }
}
