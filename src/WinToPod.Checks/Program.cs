// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using WinToPod.Core;
if (args.FirstOrDefault() == "--test-automatic-connection") return AutomaticConnectionChecks.Run();

if (args.FirstOrDefault() == "--test-digest") return await DigestChecks.Run(args);
if (args.FirstOrDefault() == "--test-stability") return await StabilityChecks.Run(args);
if (args.FirstOrDefault() == "--test-video-sync") return await VideoSyncChecks.Run();

if (args.FirstOrDefault() == "--test-native-pair")
{
    if (args.Length != 4 || !int.TryParse(args[1], out var leaderPort) || !int.TryParse(args[2], out var secondaryPort)) return 2;
    var mode = args[3];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    WinToPod.AirPlay.Media.NativePlaybackSession? playback = null;
    var sent = 0; var consentCalls = 0; var passwordPromptCalls = 0; var liveTest = new SessionSoundTest();
    var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var pairings = new WinToPod.AirPlay.Pairing.MemoryReceiverPairingStore();
    try
    {
        var local = System.Net.IPAddress.Loopback;
        var addresses = new[] { System.Net.IPAddress.Parse("127.0.0.2"), System.Net.IPAddress.Parse("127.0.0.3") };
        var options = addresses.Select((remote, i) => new WinToPod.AirPlay.Media.AudioSessionOptions(remote, i == 0 ? leaderPort : secondaryPort,
            local, null, 20, addresses, new Dictionary<string, string?> { ["model"] = "AudioAccessory5,1", ["osvers"] = "27.0", ["tsid"] = "native-pair-test", ["flags"] = i == 0 ? "0xbac04" : "0xb8c84" }, new(31319, 31320), SenderIdentity: "CA58F410-91D9-4C29-A81E-998EC4B029C1")).ToArray();
        if (mode.StartsWith("calibrated-", StringComparison.Ordinal)) options = options.Select(o => o with { VolumeCurve = WinToPod.AirPlay.Media.ReceiverVolumeCurve.DesktopCalibrated }).ToArray();
        if (mode == "password-supplied") options[1] = options[1] with { Password = "demo-secondary-secret" };
        if (mode == "password-both") options[0] = options[0] with { AdvertisedProperties = new Dictionary<string, string?>(options[0].AdvertisedProperties!) { ["flags"] = "0xbac84" } };
        if (mode is "password-live-only" or "no-password") options[1] = options[1] with { AdvertisedProperties = new Dictionary<string, string?>(options[1].AdvertisedProperties!) { ["flags"] = "0xb8c04" } };
        Task<WinToPod.AirPlay.Media.NativePlaybackSession> Connect() => WinToPod.AirPlay.Media.NativePlaybackSession.OpenAsync(mode == "fallback-remembered" ? options[..1] : options, null, timeout.Token,
            diagnostic: diagnostics.Enqueue,
            singleFallbackConsent: mode.StartsWith("fallback-", StringComparison.Ordinal) ? (failure, token) =>
            {
                if (failure.Member != 2 || (mode == "fallback-setup-rejected"
                    ? failure.InnerException is not WinToPod.AirPlay.Transport.RtspRejectedException { Status: 403 }
                    : mode == "fallback-wrong-password-no-delay"
                    ? failure.InnerException is not WinToPod.AirPlay.Pairing.PasswordRequiredException { Rejected: true }
                    : failure.InnerException is not WinToPod.AirPlay.Pairing.PairingRejectedException { Code: 2, RetryDelaySeconds: 4 })) throw new Exception("Wrong fallback cause.");
                consentCalls++; if (mode == "fallback-cancel") timeout.Cancel();
                return Task.FromResult(mode != "fallback-decline");
            } : null,
            requestPassword: mode == "password-required" ? null : (request, token) =>
            {
                passwordPromptCalls++;
                if (mode == "password-cancel-token") timeout.Cancel();
                if (mode is "password-cancel" or "password-cancel-token") return Task.FromResult<string?>(null);
                if (mode is "wrong-password" or "wrong-password-no-delay" or "fallback-wrong-password" or "fallback-wrong-password-no-delay") return Task.FromResult<string?>("wrong-pair-secret");
                if (request.RemoteAddress.Equals(addresses[0]) && mode != "password-both") throw new Exception("Unprotected leader requested a password.");
                return Task.FromResult<string?>(request.RemoteAddress.Equals(addresses[0]) ? "demo-primary-secret" : "demo-secondary-secret");
            }, pairingStore: pairings);
        playback = await Connect();
        if (mode.StartsWith("calibrated-", StringComparison.Ordinal))
        {
            foreach (var percent in new[] { 1, 2, 5, 8, 10, 15, 16, 25, 35, 50, 75, 99, 100, 0, 2, 25, 50 })
            {
                if (!await playback.SetVolumeAndConfirmAsync(percent, timeout.Token) || await playback.ReadVolumeAsync(timeout.Token) != percent)
                    throw new Exception($"Calibrated volume drift at {percent} percent.");
            }
            await playback.CloseAsync(true); Console.WriteLine("CALIBRATED_VOLUME_CONTROL_OK"); return 0;
        }
        if (mode is "cache-reconnect" or "cache-verify-rejected")
        {
            await playback.RunAsync((memory, _) => { memory.Span.Fill(3); return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(.35), timeout.Token);
            await playback.CloseAsync(true);
            playback = await Connect();
        }
        if (mode.StartsWith("fallback-", StringComparison.Ordinal))
        {
            if (consentCalls != (mode == "fallback-remembered" ? 0 : 1) || playback.MemberCount != 1 ||
                playback.IsSingleFallback != (mode != "fallback-remembered")) throw new Exception("Fallback consent was not respected.");
        }
        if (mode is "no-clock" or "reject-stream" or "volume-reject" or "reject-auth" or "bad-m6-signature" or "bad-verify-signature" or "wrong-verify-id" or "reject-verify" or "cache-verify-rejected") throw new Exception("Incomplete group became ready.");
        if (mode == "volume-change")
        {
            if (await playback.ReadVolumeAsync(timeout.Token) != 43 || !await playback.SetVolumeAndConfirmAsync(37, timeout.Token) ||
                await playback.ReadVolumeAsync(timeout.Token) != 37) throw new Exception("Volume observation/confirmation failed.");
        }
        if (mode.StartsWith("mute-", StringComparison.Ordinal))
        {
            await playback.RunAsync(async (memory, token) =>
            {
                if (sent == 32)
                {
                    if (!await playback.SetVolumeAndConfirmAsync(0, token) || await playback.ReadVolumeAsync(token) != 0) throw new Exception("Mute normalized readback tore down the session.");
                }
                if (sent == 128)
                {
                    if (!await playback.SetVolumeAndConfirmAsync(37, token) || await playback.ReadVolumeAsync(token) != 37) throw new Exception("Unmute failed.");
                }
                memory.Span.Fill(7); sent++;
            }, TimeSpan.FromSeconds(2), timeout.Token);
            if (playback.MemberPackets.Any(n => n < 180) || playback.MemberPackets.Distinct().Count() != 1) throw new Exception("Muted pair stopped streaming.");
            await playback.CloseAsync(true); Console.WriteLine("NATIVE_PAIR_MUTE_OK"); return 0;
        }
        await playback.RunAsync((memory, _) =>
        {
            // Nonzero, distinct L/R samples: receivers must get intact stereo,
            // not a guessed split or two independent source reads.
            var frame = memory.Span;
            for (var i = 0; i < frame.Length; i += 4)
            { System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(frame[i..], (short)(sent % 16000)); System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(frame[(i + 2)..], (short)(-1000 - sent % 16000)); }
            if (mode == "in-session-test")
            {
                if (sent == 32 && !liveTest.Start(playback.LatencyFrames)) throw new Exception("Test did not start in the current session.");
                liveTest.Apply(frame);
            }
            if (mode.StartsWith("fallback-", StringComparison.Ordinal)) PcmMix.ToDualMono(frame);
            sent++; return ValueTask.CompletedTask;
        }, TimeSpan.FromSeconds(mode == "in-session-test" ? 7 : mode is "interrupt" or "clock-loss" ? 5 : .9), timeout.Token);
        if (mode is "interrupt" or "clock-loss") throw new Exception("Group interruption was ignored.");
        if (mode == "in-session-test" && liveTest.Status.Active) throw new Exception("Live sound test did not finish.");
        if (playback.MemberCount != (mode.StartsWith("fallback-", StringComparison.Ordinal) ? 1 : 2) || playback.MemberPackets.Any(p => p < 30) || playback.MemberPackets.Distinct().Count() != 1)
            throw new Exception("Pair was not streamed equally.");
        Console.WriteLine("NATIVE_PAIR_OK"); return 0;
    }
    catch (WinToPod.AirPlay.Media.NativeMemberAuthenticationException e) when (mode == "setup-rejected" && sent == 0 &&
        e.Member == 2 && e.InnerException is WinToPod.AirPlay.Transport.RtspRejectedException { Status: 403 })
    { Console.WriteLine("PAIRING_START_REJECTED_NO_AUDIO_NO_RETRY"); return 0; }
    catch (WinToPod.AirPlay.Pairing.PasswordRequiredException e) when (mode == "password-required" && !e.Rejected && sent == 0)
    { Console.WriteLine("SECONDARY_PASSWORD_REQUIRED_BEFORE_SRP_NO_AUDIO"); return 0; }
    catch (OperationCanceledException) when (mode is "password-cancel" or "password-cancel-token" && sent == 0)
    { Console.WriteLine("SECONDARY_PASSWORD_CANCELLED_BEFORE_SRP_NO_AUDIO"); return 0; }
    catch (WinToPod.AirPlay.Media.NativeMemberAuthenticationException e) when (mode == "wrong-password" && sent == 0 &&
        e.Member == 2 && e.InnerException is WinToPod.AirPlay.Pairing.PairingRejectedException { Code: 2, RetryDelaySeconds: 4 })
    { Console.WriteLine("SECONDARY_WRONG_PASSWORD_BACKOFF_NO_RETRY"); return 0; }
    catch (WinToPod.AirPlay.Media.NativeMemberAuthenticationException e) when (mode == "wrong-password-no-delay" && sent == 0 &&
        e.Member == 2 && e.InnerException is WinToPod.AirPlay.Pairing.PasswordRequiredException { Rejected: true })
    { Console.WriteLine("SECONDARY_WRONG_PASSWORD_NO_RETRY"); return 0; }
    catch (System.Security.Cryptography.CryptographicException) when (mode is "bad-m6-signature" or "bad-verify-signature" or "wrong-verify-id" && sent == 0)
    { Console.WriteLine("INVALID_PAIRING_IDENTITY_STOPPED_NO_AUDIO_NO_RETRY"); return 0; }
    catch (WinToPod.AirPlay.Media.NativeMemberAuthenticationException e) when (mode is "reject-verify" or "cache-verify-rejected" && sent == 0 &&
        e.Member == 2 && e.InnerException is WinToPod.AirPlay.Pairing.PairingRejectedException { Code: 2, RetryDelaySeconds: 4 })
    { Console.WriteLine("VERIFY_REJECTED_NO_PASSWORD_REPAIR_NO_RETRY"); return 0; }
    catch (WinToPod.AirPlay.Media.NativeMemberAuthenticationException e) when (mode is "reject-m6" or "reject-verify-no-delay" && sent == 0 &&
        e.Member == 2 && e.InnerException is WinToPod.AirPlay.Pairing.PairingRejectedException { Code: 2, RetryDelaySeconds: null })
    { Console.WriteLine("POST_SRP_REFUSAL_NOT_MISLABELLED_AS_WRONG_PASSWORD"); return 0; }
    catch (WinToPod.AirPlay.Media.NativeMemberAuthenticationException e) when (mode is "reject-auth" or "fallback-decline" && sent == 0 &&
        e.Member == 2 && e.InnerException is WinToPod.AirPlay.Pairing.PairingRejectedException { Code: 2, RetryDelaySeconds: 4 })
    { if (mode == "fallback-decline" && consentCalls != 1) throw new Exception("Missing consent prompt."); Console.WriteLine("NATIVE_PAIR_AUTH_REJECTED_BEFORE_SETUP_NO_RETRY"); return 0; }
    catch (OperationCanceledException) when (mode == "fallback-cancel" && sent == 0 && consentCalls == 1)
    { Console.WriteLine("FALLBACK_CANCELLED_NO_AUDIO"); return 0; }
    catch (IOException) when (mode == "fallback-volume-reject" && sent == 0 && consentCalls == 1)
    { Console.WriteLine("FALLBACK_VOLUME_NOT_CONFIRMED_NO_AUDIO"); return 0; }
    catch (IOException) when (mode is "no-clock" or "reject-stream" or "volume-reject" && sent == 0)
    { Console.WriteLine("NATIVE_PAIR_REJECTED_NO_AUDIO"); return 0; }
    catch (IOException) when (mode is "interrupt" or "clock-loss" && sent > 30)
    {
        try { await playback!.SetVolumeAndConfirmAsync(80, timeout.Token); throw new Exception("Volume allowed after ownership loss."); }
        catch (IOException) { }
        Console.WriteLine("NATIVE_PAIR_STOPPED_NO_CONTROL_NO_RETRY"); return 0;
    }
    finally
    {
        if (playback is not null) await playback.DisposeAsync();
        var expectedPrompts = mode is "fallback-remembered" or "password-required" or "password-supplied" or "no-password" ? 0 : mode == "password-both" ? 2 : 1;
        if (passwordPromptCalls != expectedPrompts) throw new Exception($"Unexpected password prompt count: {passwordPromptCalls}, expected {expectedPrompts}.");
        if (mode == "bad-m6-signature" && pairings.SavedCount != 0) throw new Exception("Unauthenticated pairing was saved.");
        if (mode is "cache-reconnect" or "cache-verify-rejected" && pairings.SavedCount != 1) throw new Exception("Pairing was not retained for reconnection.");
        var trace = string.Join('\n', diagnostics);
        if (new[] { "demo-primary-secret", "demo-secondary-secret", "wrong-pair-secret" }.Any(trace.Contains)) throw new Exception("Password leaked into diagnostics.");
    }
}

if (args.FirstOrDefault() == "--test-session")
{
    if (args.Length != 3 || !int.TryParse(args[1], out var port) || args[2] is not ("normal" or "reject-volume" or "interrupt" or "password-required" or "password-supplied" or "wrong-password" or "access-denied" or "password-challenge" or "password-challenge-supplied" or "info-denied" or "pairing-denied-tlv" or "ptp-pair" or "ptp-single" or "ptp-no-clock" or "ptp-probes-only" or "ptp-clock-loss" or "ptp-volume-reject" or "ptp-reject-peers" or "ptp-port-busy" or "mute-single-floor" or "mute-single-stale")) return 2;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    WinToPod.AirPlay.Media.RealtimeAudioSession? session = null;
    var protocolDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
    try
    {
        var password = args[2] is "password-supplied" or "password-challenge-supplied" ? "demo-secret" : args[2] == "wrong-password" ? "wrong" : null;
        var ptp = args[2].StartsWith("ptp-", StringComparison.Ordinal);
        var remote = ptp ? System.Net.IPAddress.Parse("127.0.0.2") : System.Net.IPAddress.Loopback;
        var fields = new Dictionary<string, string?> { ["model"] = "AudioAccessory5,1", ["osvers"] = "27.0", ["igl"] = "1" };
        if (args[2] is not ("ptp-single" or "ptp-probes-only")) fields["tsid"] = "synthetic-native-pair";
        var peers = args[2] == "ptp-pair" ? new[] { remote, System.Net.IPAddress.Parse("127.0.0.3") } : new[] { remote };
        session = await WinToPod.AirPlay.Media.RealtimeAudioSession.OpenAsync(new(remote, port, System.Net.IPAddress.Loopback, password, 20, peers, ptp ? fields : null, ptp ? new(31319, 31320) : null), null, timeout.Token, protocolDiagnostics.Enqueue);
        if (args[2] is "password-required" or "wrong-password" or "access-denied" or "password-challenge" or "password-challenge-supplied" or "info-denied" or "pairing-denied-tlv") throw new Exception("Unexpected authentication success.");
        if (args[2] == "reject-volume") throw new Exception("Unconfirmed volume unexpectedly allowed audio.");
        if (args[2] is "ptp-no-clock" or "ptp-probes-only" or "ptp-volume-reject" or "ptp-reject-peers" or "ptp-port-busy") throw new Exception("Unexpected PTP acquisition success.");
        if (args[2].StartsWith("mute-single-", StringComparison.Ordinal))
        {
            var count = 0;
            await session.RunAsync(async (memory, token) =>
            {
                if (count == 32 && (!await session.SetVolumeAndConfirmAsync(0, token) || await session.ReadVolumeAsync(token) != 0)) throw new Exception("Single mute failed.");
                if (count == 128 && !await session.SetVolumeAndConfirmAsync(20, token)) throw new Exception("Single unmute failed.");
                memory.Span.Fill(7); count++;
            }, TimeSpan.FromSeconds(2), timeout.Token);
        }
        else
        await session.RunAsync((memory, _) => { memory.Span.Clear(); return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(args[2] is "interrupt" or "ptp-clock-loss" ? 5 : 0.9), timeout.Token);
        if (session.LastConfirmedVolume != 20 || session.PacketsSent < 30) throw new Exception("Simulated session did not stream.");
        Console.WriteLine("SESSION_OK packets=" + session.PacketsSent + " resends=" + session.Retransmits); return 0;
    }
    catch (WinToPod.AirPlay.Pairing.PasswordRequiredException e) when (args[2] is "password-required" or "password-challenge" && !e.Rejected) { Console.WriteLine("PASSWORD_REQUIRED_NO_AUDIO"); return 0; }
    catch (WinToPod.AirPlay.Pairing.PasswordRequiredException e) when (args[2] == "wrong-password" && e.Rejected) { Console.WriteLine("PASSWORD_REJECTED_NO_RETRY"); return 0; }
    catch (WinToPod.AirPlay.Pairing.PairingRejectedException e) when (args[2] == "pairing-denied-tlv" && e.Code == 6 && e.RetryDelaySeconds == 60 && e.ResponseStatus == 403)
    {
        var trace = string.Join('\n', protocolDiagnostics);
        if (!trace.Contains("POST /pair-setup → 403") || !trace.Contains("retryDelay=60") || trace.Contains("never-log")) throw new Exception("Pairing backoff evidence missing or secret leaked.");
        Console.WriteLine("REJECTED_WITH_DEVICE_BACKOFF_NO_RETRY"); return 0;
    }
    catch (WinToPod.AirPlay.Transport.RtspRejectedException e) when (args[2] is "access-denied" or "info-denied" or "pairing-denied-tlv" && e.Status == 403)
    {
        var trace = string.Join('\n', protocolDiagnostics);
        if (!trace.Contains("POST /pair-setup → 403")) throw new Exception("Missing pairing rejection evidence.");
        if (args[2] == "info-denied" && !trace.Contains("GET /info → 403")) throw new Exception("Missing info rejection evidence.");
        if (args[2] == "pairing-denied-tlv" && (!trace.Contains("tlvError=6") || !trace.Contains("retryDelay=60"))) throw new Exception("Missing numeric rejection evidence.");
        if (trace.Contains("never-log") || trace.Contains("private-home")) throw new Exception("Diagnostic secret leak.");
        Console.WriteLine("REJECTED_NO_PASSWORD_PROMPT_NO_RETRY; " + trace); return 0;
    }
    catch (IOException e) when (args[2] == "password-challenge-supplied" && e.Message.Contains("另一种鉴权方式")) { Console.WriteLine("UNSUPPORTED_AUTH_NO_REPEAT_PROMPT"); return 0; }
    catch (IOException) when (args[2] == "reject-volume") { Console.WriteLine("VOLUME_REJECTED"); return 0; }
    catch (IOException) when (args[2] == "interrupt" && session?.PacketsSent > 30) { Console.WriteLine("SESSION_INTERRUPTED_NO_RETRY"); return 0; }
    catch (IOException e) when (args[2] is "ptp-no-clock" or "ptp-probes-only" && e.Message.Contains("尚未同步")) { Console.WriteLine("PTP_NOT_READY_NO_AUDIO_NO_VOLUME"); return 0; }
    catch (IOException) when (args[2] == "ptp-clock-loss" && session?.PacketsSent > 30) { Console.WriteLine("PTP_LOST_NO_RETRY"); return 0; }
    catch (IOException) when (args[2] == "ptp-volume-reject") { Console.WriteLine("PTP_VOLUME_REJECTED_NO_AUDIO"); return 0; }
    catch (WinToPod.AirPlay.Transport.RtspRejectedException e) when (args[2] == "ptp-reject-peers" && e.Stage == "设置时钟节点") { Console.WriteLine("PTP_PEERS_REJECTED_NO_AUDIO"); return 0; }
    catch (IOException e) when (args[2] == "ptp-port-busy" && e.Message.Contains("PTP时钟端口")) { Console.WriteLine("PTP_PORT_BUSY_NO_FALLBACK"); return 0; }
    finally { if (session is not null) await session.DisposeAsync(); }
}

var checks = new List<(string Name, Func<Task> Run)>();
void Check(string name, Func<Task> run) => checks.Add((name, run));
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void True(bool actual) { if (!actual) throw new Exception("Expected true."); }
Check("单只兼容合并：左右内容均保留、双通道相同、极值不溢出", () =>
{
    var bytes = new byte[16];
    var samples = new short[] { 2000, 0, 0, -6000, short.MaxValue, short.MaxValue, short.MinValue, short.MinValue };
    for (var i = 0; i < samples.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
    PcmMix.ToDualMono(bytes);
    var expected = new short[] { 1000, 1000, -3000, -3000, short.MaxValue, short.MaxValue, short.MinValue, short.MinValue };
    for (var i = 0; i < expected.Length; i++) Equal(expected[i], System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)));
    return Task.CompletedTask;
});
Check("接收端等待：副成员冷却不阻止主成员，组合等待保留且不会被较短回复缩短", () =>
{
    var now = DateTimeOffset.Parse("2026-10-17T05:00:00Z"); var saved = new Dictionary<string, DateTimeOffset>(); var retry = new ReceiverRetrySchedule(saved);
    retry.Record("white", 256, now); retry.Record("white", 1, now.AddSeconds(1));
    Equal(0L, retry.Remaining(["black"], now)); Equal(201L, retry.Remaining(["black", "white"], now.AddSeconds(55)));
    var restored = new ReceiverRetrySchedule(JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(JsonSerializer.Serialize(saved))!);
    Equal(128L, restored.Remaining(["white"], now.AddSeconds(128))); Equal(0L, restored.Remaining(["white"], now.AddSeconds(256)));
    retry.Record("black", ulong.MaxValue, now); True(retry.Remaining(["black"], now) > 0); return Task.CompletedTask;
});
Check("连接内测试：按左、右、一起播放，经过缓冲恢复实时采集且不重复启动", () =>
{
    var test = new SessionSoundTest(); True(test.Start(88200)); True(!test.Start(88200)); True(test.Status.Description.Contains("即将"));
    var block = new byte[44100 * 4 / 2];
    for (var segment = 0; segment < 9; segment++)
    {
        block.AsSpan().Fill(77); test.Apply(block);
        var left = false; var right = false;
        for (var at = 0; at < block.Length; at += 4)
        {
            left |= System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(block.AsSpan(at)) != 0;
            right |= System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(block.AsSpan(at + 2)) != 0;
        }
        Equal(segment < 3 || segment >= 6, left); Equal(segment >= 3, right);
    }
    for (var i = 0; i < 4; i++) { block.AsSpan().Fill(77); test.Apply(block); True(block.All(b => b == 77)); }
    True(!test.Status.Active); True(test.Start(88200)); return Task.CompletedTask;
});
Check("连接内测试：取消后不再生成测试音，排空原有缓冲；失去连接立即重置", () =>
{
    var test = new SessionSoundTest(); True(test.Start(88200)); var block = new byte[1408]; test.Apply(block); test.Stop();
    True(test.Status.Active && !test.Status.CanStop); block.AsSpan().Fill(99); test.Apply(block); True(block.All(b => b == 99));
    test.Reset(); True(!test.Status.Active); True(test.Start(88200)); return Task.CompletedTask;
});
Check("连接内测试：跨帧结束时保留本帧末尾的实时音频", () =>
{
    var test = new SessionSoundTest(); True(test.Start(0)); var whole = new byte[44100 * 4 * 4]; test.Apply(whole);
    var crossing = new byte[(44100 / 2 + 5) * 4]; crossing.AsSpan().Fill(123); test.Apply(crossing);
    True(crossing.AsSpan(crossing.Length - 20).ToArray().All(b => b == 123)); True(!test.Status.Active); return Task.CompletedTask;
});
checks.AddRange(AirPlayChecks.All());
checks.AddRange(PrivacyChecks.All());
checks.AddRange(DiscoveryChecks.All());
checks.AddRange(SessionChecks.All());
checks.AddRange(VolumeConfirmationChecks.All());
checks.AddRange(PtpChecks.All());
checks.Add(("PCM缓冲：下溢补静音、溢出丢最旧整帧、内存有界", () =>
{
    var buffer = new PcmBuffer(1408); var data = Enumerable.Range(0, 1408).Select(i => (byte)i).ToArray();
    buffer.Write(data); var first = new byte[8]; buffer.Read(first); True(first.SequenceEqual(data[..8]));
    buffer.Write(new byte[1408]); Equal(350L, buffer.DroppedFrames); Equal(1408, buffer.AvailableBytes);
    var output = new byte[1412]; buffer.Read(output); True(output.All(b => b == 0)); Equal(1L, buffer.MissingFrames);
    buffer.Write(data); buffer.Clear(); Equal(0, buffer.AvailableBytes); return Task.CompletedTask;
}));
var failures = 0;
foreach (var (name, run) in checks)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine("PASS " + name); }
    catch (Exception error) { ++failures; Console.WriteLine("FAIL " + name + "\n" + error); }
}
Console.WriteLine($"RESULT {checks.Count - failures}/{checks.Count} passed; real_audio=false; windows_ui=false");
return failures == 0 ? 0 : 1;
