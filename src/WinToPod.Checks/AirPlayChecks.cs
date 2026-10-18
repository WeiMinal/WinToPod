// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using WinToPod.AirPlay;

internal static class AirPlayChecks
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Plain = Encoding.ASCII.GetBytes("GET /info RTSP/1.0\r\nCSeq: 1\r\n\r\n");
    // Independently generated with Python cryptography, not captured from Apple hardware.
    private const string Frame0 = "1f005ffd1611828fc8b77c410e35fc136116d681feffa2fe3e2dd6d8cd1f20387f15cfe9b2c307ac3eab604690f9f33f23";
    private const string Frame1 = "1f00d812a77f9a43c17e4696fc0801c5b6b6eb4de604043805dcf944845687e5d792aa03da9af33300ab0973641490af17";

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Wrap("AirPlay特征：低32位在前、高32位在后，保留未知位", () =>
        {
            True(AirPlayFeatures.TryParse("0x5A7FDFD5,0x3C155FDE", out var value));
            True(value.Bits == 0x3C155FDE5A7FDFD5UL);
            True(AirPlayFeatures.TryParse("0,80000000", out var high) && high.HasBit(63));
            True(AirPlayFeatures.TryParse(" 0x1 ", out var low) && low.Bits == 1);
        });
        yield return Wrap("AirPlay特征：损坏/溢出广告拒绝解析，不伪造能力", () =>
        {
            foreach (var text in new string?[] { null, "", " ", "0x", "-1", "+1", "100000000", "1,100000000", "1,2,3", "1,", "garbage" })
                True(!AirPlayFeatures.TryParse(text, out _));
        });
        yield return Wrap("AirPlay特征：越界位号不会发生移位回绕", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => new AirPlayFeatures(1).HasBit(-1));
            Throws<ArgumentOutOfRangeException>(() => new AirPlayFeatures(1).HasBit(64));
        });
        yield return Wrap("控制帧：两个连续nonce匹配独立实现固定答案", () =>
        {
            using var sender = new ControlFrameCipher(Key);
            True(Convert.ToHexStringLower(sender.Encrypt(Plain)) == Frame0);
            True(Convert.ToHexStringLower(sender.Encrypt(Plain)) == Frame1);
            using var receiver = new ControlFrameCipher(Key);
            True(receiver.Decrypt(Convert.FromHexString(Frame0)).SequenceEqual(Plain));
            True(receiver.Decrypt(Convert.FromHexString(Frame1)).SequenceEqual(Plain));
        });
        yield return Wrap("控制帧：认证标签被修改后关闭该方向，不能继续猜nonce", () =>
        {
            using var receiver = new ControlFrameCipher(Key);
            var corrupted = Convert.FromHexString(Frame0); corrupted[^1] ^= 1;
            Throws<CryptographicException>(() => receiver.Decrypt(corrupted));
            Throws<InvalidOperationException>(() => receiver.Decrypt(Convert.FromHexString(Frame0)));
        });
        yield return Wrap("控制帧：重复报文不能作为新报文接受", () =>
        {
            using var receiver = new ControlFrameCipher(Key);
            var frame = Convert.FromHexString(Frame0); receiver.Decrypt(frame);
            Throws<CryptographicException>(() => receiver.Decrypt(frame));
        });
        yield return Wrap("控制帧：长度头和实际长度必须一致", () =>
        {
            using var receiver = new ControlFrameCipher(Key);
            var frame = Convert.FromHexString(Frame0); frame[0]++;
            Throws<InvalidDataException>(() => receiver.Decrypt(frame));
            using var truncated = new ControlFrameCipher(Key);
            Throws<InvalidDataException>(() => truncated.Decrypt(new byte[17]));
        });
        yield return Wrap("控制帧：超长报文在解密前被拒绝", () =>
        {
            using var receiver = new ControlFrameCipher(Key, 16);
            Throws<InvalidDataException>(() => receiver.Decrypt(Convert.FromHexString(Frame0)));
        });
        yield return Wrap("控制帧：空内容与最大默认帧可用；越界输入不消耗nonce", () =>
        {
            using var sender = new ControlFrameCipher(Key);
            using var receiver = new ControlFrameCipher(Key);
            Throws<ArgumentOutOfRangeException>(() => sender.Encrypt(new byte[1025]));
            True(receiver.Decrypt(sender.Encrypt([])).Length == 0);
            var maximum = new byte[1024]; maximum[^1] = 1;
            True(receiver.Decrypt(sender.Encrypt(maximum)).SequenceEqual(maximum));
        });
        yield return Wrap("控制帧：无效密钥/长度限制拒绝创建", () =>
        {
            Throws<ArgumentException>(() => new ControlFrameCipher(new byte[31]));
            Throws<ArgumentOutOfRangeException>(() => new ControlFrameCipher(Key, 0));
            Throws<ArgumentOutOfRangeException>(() => new ControlFrameCipher(Key, 65536));
        });
        yield return Wrap("HKDF：双方向派生匹配独立固定答案且互不相同", () =>
        {
            var secret = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var write = ControlFrameCipher.DeriveControlKey(secret, true);
            var read = ControlFrameCipher.DeriveControlKey(secret, false);
            True(Convert.ToHexStringLower(write) == "5a6cb19bcbe7d4df2dd8279f39562f7fae2dbf73eb5a4f98849c245c82b2fe96");
            True(Convert.ToHexStringLower(read) == "c2f56f1912e2fc1f6604fd5bccab619272f687345aa4bce1c3c857421bd3b821");
            True(!write.SequenceEqual(read));
            Throws<ArgumentException>(() => ControlFrameCipher.DeriveControlKey([], true));
            CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(write); CryptographicOperations.ZeroMemory(read);
        });
    }

    private static (string Name, Func<Task> Run) Wrap(string name, Action action)
        => (name, () => { action(); return Task.CompletedTask; });
    private static void True(bool value) { if (!value) throw new Exception("Protocol check failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
