// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Pairing;
using WinToPod.AirPlay.Transport;

internal static class DigestChecks
{
    public static async Task<int> Run(string[] args)
    {
        var mode = args[3]; var single = mode.StartsWith("single-", StringComparison.Ordinal);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var store = new MemoryReceiverPairingStore();
        var traces = new ConcurrentQueue<string>(); var prompts = new List<bool>(); var consents = 0; var frames = 0;
        var local = IPAddress.Loopback; var addresses = new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3") };
        var options = addresses.Select((ip, i) => new AudioSessionOptions(ip, int.Parse(args[i + 1]), local, null, 20,
            single ? [addresses[1]] : addresses,
            new Dictionary<string, string?> { ["features"] = "0x4A7FCA00,0x3C354BD0", ["flags"] = i == 1 && mode is not ("single-transient" or "cancel-playback") ? "0xb8c84" : "0xbac04" },
            new(31319, 31320), SenderIdentity: "CA58F410-91D9-4C29-A81E-998EC4B029C1", ReceiverId: i.ToString())).ToArray();
        NativePlaybackSession? playback = null;
        Task<NativePlaybackSession> Connect() => NativePlaybackSession.OpenAsync(single ? options[1..] : options, null, stop.Token, traces.Enqueue,
            singleFallbackConsent: mode is "fallback-accept" or "fallback-decline" ? (failure, token) =>
            {
                if (failure.Member != 2 || !failure.DuringPlayback || failure.InnerException is not PlaybackAuthenticationException) throw new Exception("Wrong fallback stage.");
                consents++; return Task.FromResult(mode == "fallback-accept");
            } : null,
            requestPassword: (request, token) =>
            {
                if (!request.RemoteAddress.Equals(addresses[1])) throw new Exception("Password requested for unprotected sibling.");
                prompts.Add(request.ForPlayback);
                if (mode == "cancel-playback") { stop.Cancel(); return Task.FromResult<string?>(null); }
                return Task.FromResult<string?>("demo-secondary-secret");
            }, pairingStore: store);
        try
        {
            playback = await Connect();
            if (mode == "cache-reconnect")
            {
                await playback.CloseAsync(true); playback = await Connect();
                if (!prompts.SequenceEqual(new[] { false, true }) || store.SavedCount != 1) throw new Exception("Cached identity did not bypass SRP, or playback password was incorrectly persisted.");
            }
            if (mode is "reject-proof" or "fallback-decline" or "malformed" or "unsupported-qop") throw new Exception("Refused playback became ready.");
            await playback.RunAsync((memory, token) => { memory.Span.Fill(3); frames++; return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(mode == "late-refusal" ? 5 : .6), stop.Token);
            if (mode == "late-refusal") throw new Exception("Late refusal ignored.");
            if (frames < 30 || playback.MemberCount != (single || mode == "fallback-accept" ? 1 : 2)) throw new Exception("Audio not delivered to expected members.");
            if (mode == "fallback-accept" && (!playback.IsSingleFallback || consents != 1)) throw new Exception("Single fallback lacked consent.");
        }
        catch (NativeMemberAuthenticationException error) when (mode is "reject-proof" or "fallback-decline" or "malformed" or "unsupported-qop" && error.DuringPlayback && frames == 0)
        { if (mode == "fallback-decline" && consents != 1) throw new Exception("Missing decline choice."); }
        catch (OperationCanceledException) when (mode == "cancel-playback" && frames == 0 && prompts.SequenceEqual(new[] { true })) { }
        catch (IOException) when (mode == "late-refusal" && frames > 30)
        {
            try { await playback!.SetVolumeAndConfirmAsync(80, stop.Token); throw new Exception("Control after loss."); } catch (IOException) { }
        }
        finally { if (playback is not null) await playback.DisposeAsync(); }
        var expected = mode == "cache-reconnect" ? 2 : 1;
        if (prompts.Count != expected) throw new Exception("Playback challenge repeated a password prompt.");
        if (mode != "cache-reconnect" && prompts[0] != (mode is "single-transient" or "cancel-playback")) throw new Exception("Wrong password phase.");
        var trace = string.Join('\n', traces);
        if (new[] { "demo-secondary-secret", "never-log-digest-nonce", "never-log-opaque", "AirPlay, living-room", "Authorization:" }.Any(trace.Contains)) throw new Exception("Playback credential leaked in diagnostics.");
        Console.WriteLine("DIGEST_SCENARIO_OK; " + mode); return 0;
    }
}
