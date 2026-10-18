// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Pairing;

// HAP TLV8 messages, including 255-byte continuation fragments.
public static class Tlv8
{
    public const byte Method = 0, Identifier = 1, Salt = 2, PublicKey = 3, Proof = 4,
        EncryptedData = 5, State = 6, Error = 7, RetryDelay = 8, Signature = 10, Flags = 19;

    public static byte[] Encode(params (byte Tag, byte[] Value)[] values)
    {
        using var result = new MemoryStream();
        foreach (var (tag, value) in values)
        {
            if (value.Length > 8192) throw new ArgumentException("TLV value exceeds limit.");
            if (value.Length == 0) { result.WriteByte(tag); result.WriteByte(0); }
            for (var offset = 0; offset < value.Length; offset += 255)
            {
                var count = Math.Min(255, value.Length - offset);
                result.WriteByte(tag); result.WriteByte((byte)count); result.Write(value, offset, count);
            }
        }
        return result.ToArray();
    }

    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 32768) throw new InvalidDataException("TLV message exceeds limit.");
        var fields = new Dictionary<byte, byte[]>(); var offset = 0; int previous = -1, previousLength = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 2) throw new InvalidDataException("Incomplete TLV header.");
            var tag = bytes[offset++]; var count = bytes[offset++];
            if (count > bytes.Length - offset) throw new InvalidDataException("Incomplete TLV value.");
            var value = bytes.Slice(offset, count).ToArray(); offset += count;
            if (fields.TryGetValue(tag, out var old))
            {
                if (tag != previous || previousLength != 255 || old.Length + count > 8192)
                    throw new InvalidDataException("Ambiguous duplicate TLV field.");
                fields[tag] = old.Concat(value).ToArray();
            }
            else fields[tag] = value;
            previous = tag; previousLength = count;
        }
        return fields;
    }

    public static byte[] Require(this IReadOnlyDictionary<byte, byte[]> fields, byte tag, int minimum = 1, int maximum = 8192)
    {
        if (!fields.TryGetValue(tag, out var value) || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException($"Missing or invalid pairing field {tag}.");
        return value;
    }

    public static void RequireState(this IReadOnlyDictionary<byte, byte[]> fields, byte expected)
    {
        fields.ThrowIfError();
        if (fields.Require(State, 1, 1)[0] != expected) throw new InvalidDataException("Unexpected pairing state.");
    }

    public static void ThrowIfError(this IReadOnlyDictionary<byte, byte[]> fields, int? responseStatus = null)
    {
        if (fields.TryGetValue(Error, out var error))
        {
            ulong? retry = null;
            if (fields.TryGetValue(RetryDelay, out var delay) && delay.Length is > 0 and <= 4)
            { ulong seconds = 0; for (var i = 0; i < delay.Length; i++) seconds |= (ulong)delay[i] << (8 * i); retry = seconds; }
            throw new PairingRejectedException(error.Length == 1 ? error[0] : -1, retry, responseStatus);
        }
    }
}

public sealed class PairingRejectedException(int code, ulong? retryDelaySeconds = null, int? responseStatus = null) : IOException($"音箱拒绝鉴权（TLV错误 {code}），原因尚未确认。已停止连接，不会自动重复尝试。")
{
    public int Code { get; } = code;
    public ulong? RetryDelaySeconds { get; } = retryDelaySeconds;
    public int? ResponseStatus { get; } = responseStatus;
    // Start the countdown when the refusal arrives, never when a user later
    // closes a fallback/password dialog or when another exception wraps it.
    public DateTimeOffset ReceivedAtUtc { get; } = DateTimeOffset.UtcNow;
}
