// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Pairing;
using WinToPod.AirPlay.Transport;

internal static class StabilityChecks
{
    public static async Task<int> Run(string[] args)
    {
        var mode = args[3]; var seconds = mode == "soak" ? int.Parse(args[4]) : 2;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 35));
        using var pairings = new MemoryReceiverPairingStore(); using var credentials = new MemoryPlaybackCredentialStore();
        var trace = new ConcurrentQueue<string>(); var prompts = new List<bool>(); var frames = 0;
        var ips = new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3") };
        var options = ips.Select((ip, i) => new AudioSessionOptions(ip, int.Parse(args[i + 1]), IPAddress.Loopback, null, 20, ips,
            new Dictionary<string, string?> { ["features"] = "0x4A7FCA00,0x3C354BD0", ["flags"] = i == 0 ? "0xbac04" : "0xb8c84" },
            new(31319, 31320), SenderIdentity: "CA58F410-91D9-4C29-A81E-998EC4B029C1", ReceiverId: i.ToString())).ToArray();
        NativePlaybackSession? playback = null;
        Task<NativePlaybackSession> Connect() => NativePlaybackSession.OpenAsync(options, null, stop.Token, trace.Enqueue,
            requestPassword: (request, token) => { prompts.Add(request.ForPlayback); if (!request.RemoteAddress.Equals(ips[1])) throw new Exception("Sibling password disclosure."); return Task.FromResult<string?>("demo-secondary-secret"); },
            pairingStore: pairings, playbackCredentials: credentials);
        try
        {
            playback = await Connect();
            if (mode is "cached-reconnect" or "forget-reconnect" or "expired-cache")
            {
                await playback.RunAsync((pcm, _) => { pcm.Span.Fill(3); return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(.3), stop.Token);
                await playback.CloseAsync(true);
                if (mode == "forget-reconnect") credentials.Forget("1");
                if (mode == "expired-cache")
                {
                    try { playback = await Connect(); throw new Exception("Expired credential was accepted."); }
                    catch (NativeMemberAuthenticationException e) when (e.DuringPlayback) { }
                    if (prompts.Count != 1 || pairings.SavedCount != 1) throw new Exception("Cache refusal prompted/re-paired automatically.");
                }
                playback = await Connect();
                if (!prompts.SequenceEqual(mode == "cached-reconnect" ? new[] { false } : new[] { false, true }) || pairings.SavedCount != 1)
                    throw new Exception("Playback cache and pairing identity were conflated.");
            }
            await playback.RunAsync((pcm, token) => { pcm.Span.Fill(3); frames++; return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(seconds), stop.Token);
            if (mode is "event-eof" or "bad-event-tag" or "oversized-event") throw new Exception("Broken event channel was ignored.");
            if (frames < 30 || playback.MemberPackets.Any(x => x < 30)) throw new Exception("No stereo audio.");
        }
        catch (IOException error) when (mode is "event-eof" or "bad-event-tag" or "oversized-event")
        {
            var failure = SessionInterruption.Find(error);
            if (failure?.Stage != "events") throw new Exception("Missing exact event interruption cause.", error);
            if (mode == "event-eof" && failure.Category != "remote-eof") throw new Exception("EOF cause lost.");
            if (playback is not null)
                try { await playback.SetVolumeAndConfirmAsync(80, stop.Token); throw new Exception("Control after interruption."); } catch (IOException) { }
        }
        finally { if (playback is not null) await playback.CloseAsync(mode is not ("event-eof" or "bad-event-tag" or "oversized-event")); }
        if (mode is not ("cached-reconnect" or "forget-reconnect" or "expired-cache") && !prompts.SequenceEqual(new[] { false })) throw new Exception("Unexpected extra password.");
        var text = string.Join('\n', trace);
        if (new[] { "demo-secondary-secret", "never-log-event-content", "never-log-digest-nonce", "never-log-opaque" }.Any(text.Contains)) throw new Exception("Secret or event content logged.");
        if (mode is "event-eof" or "bad-event-tag" or "oversized-event" && !text.Contains("stage=events")) throw new Exception("Causal diagnostic absent.");
        Console.WriteLine("STABILITY_OK " + mode + " frames=" + frames); return 0;
    }
}
