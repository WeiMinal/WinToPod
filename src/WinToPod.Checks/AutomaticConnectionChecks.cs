// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using WinToPod.Core;

internal static class AutomaticConnectionChecks
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T00:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; public void Advance(int seconds) => Now = Now.AddSeconds(seconds); }
    public static int Run()
    {
        var passed = new List<string>();
        void Check(string name, bool value) { if (!value) throw new InvalidOperationException(name); passed.Add(name); }
        try
        {
            var time = new Clock(); var p = new AutomaticConnectionPolicy(ConnectionPolicy.Conservative, true, "native-pair", time);
            Check("startup never seizes before local session/input", p.TryBegin(true, true) is null);
            p.ObserveSession(true); Check("console session alone is insufficient", p.TryBegin(true, true) is null);
            p.ObserveLocalIntent(); Check("startup waits for discovery", p.TryBegin(true, false) is null);
            Check("brief debounce leaves explicit device clicks in control", p.TryBegin(true, true) is null); time.Advance(2);
            Check("local startup connects last target exactly once", p.TryBegin(true, true) is { TargetId: "native-pair", Reason: AutomaticConnectionReason.Startup } && p.TryBegin(true, true) is null);
            p.Connected("native-pair"); p.Interrupted("native-pair"); time.Advance(60); p.ObserveLocalIntent();
            Check("conservative never reuses startup intent after takeover", p.TryBegin(true, true) is null);
            p.Configure(ConnectionPolicy.Aggressive, true); Check("changing mode does not itself reclaim", p.TryBegin(true, true) is null);
            p.Interrupted("native-pair"); Check("aggressive obeys initial delay", p.TryBegin(true, true) is null); time.Advance(10);
            Check("aggressive can reclaim with recent physical intent", p.TryBegin(true, true) is { Reason: AutomaticConnectionReason.Reclaim });
            p.AttemptFailed(false, time.Now.AddMinutes(3)); time.Advance(90); p.ObserveLocalIntent();
            Check("receiver cooldown beats local retry interval", p.TryBegin(true, true) is null); time.Advance(90); p.ObserveLocalIntent();
            Check("cooldown expiry permits one retry", p.TryBegin(true, true) is not null);
            p.AttemptFailed(false); time.Advance(90); p.ObserveLocalIntent(); Check("last budgeted attempt can run", p.TryBegin(true, true) is not null);
            p.AttemptFailed(false); time.Advance(300); p.ObserveLocalIntent(); Check("retry budget prevents endless TV ping-pong", p.TryBegin(true, true) is null);
            p.ManualConnect(); p.Connected("native-pair"); p.Interrupted("native-pair"); time.Advance(10); p.ObserveSession(false); p.ObserveLocalIntent();
            Check("RDP or lock suppresses aggressive actions", p.TryBegin(true, true) is null);
            p.ObserveSession(true); Check("returning to console needs new local input", p.TryBegin(true, true) is null);
            p.ObserveLocalIntent(); time.Advance(91); Check("old local activity cannot authorize later remote use", p.TryBegin(true, true) is null);
            p.ObserveLocalIntent(); p.ObserveUntrustedIntent(); Check("untrusted device input invalidates local intent", p.TryBegin(true, true) is null);
            p.ObserveLocalIntent(); p.Pause(); Check("manual stop dominates both modes", p.TryBegin(true, true) is null);
            var disabled = new AutomaticConnectionPolicy(ConnectionPolicy.Conservative, false, "saved", time); disabled.ObserveSession(true); disabled.ObserveLocalIntent();
            Check("startup switch can disable startup acquisition", disabled.TryBegin(true, true) is null);
            var missing = new AutomaticConnectionPolicy(ConnectionPolicy.Conservative, true, null, time); missing.ObserveSession(true); missing.ObserveLocalIntent();
            Check("never chooses a first/random receiver", missing.TryBegin(true, true) is null);
            var auth = new AutomaticConnectionPolicy(ConnectionPolicy.Aggressive, true, "saved", time); auth.ObserveSession(true); auth.ObserveLocalIntent(); time.Advance(2); Check("authentication scenario began a real policy attempt", auth.TryBegin(true, true) is not null); auth.AttemptFailed(true); time.Advance(200); auth.ObserveLocalIntent();
            Check("authentication never becomes automatic re-pair loop", auth.TryBegin(true, true) is null);
            var cycle = new AutomaticConnectionPolicy(ConnectionPolicy.Aggressive, false, "saved", time); cycle.ObserveSession(true);
            for (var i = 0; i < 3; i++) { cycle.Connected("saved"); cycle.Interrupted("saved"); time.Advance(100); cycle.ObserveLocalIntent(); Check("successful reclaim budget " + i, cycle.TryBegin(true, true) is not null); }
            cycle.Connected("saved"); cycle.Interrupted("saved"); time.Advance(200); cycle.ObserveLocalIntent(); Check("successful takeovers also consume retry budget", cycle.TryBegin(true, true) is null);
            var cancel = new AutomaticConnectionPolicy(ConnectionPolicy.Aggressive, true, "saved", time); cancel.ObserveSession(true); cancel.ObserveLocalIntent(); time.Advance(2);
            Check("startup disable scenario begins in-flight attempt", cancel.TryBegin(true, true) is { Reason: AutomaticConnectionReason.Startup });
            cancel.Configure(ConnectionPolicy.Aggressive, false); cancel.AttemptCancelled(); time.Advance(100); cancel.ObserveLocalIntent();
            Check("disabling in-flight startup cannot turn into aggressive reclaim", !cancel.HasPending && cancel.TryBegin(true, true) is null);
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = passed, windows_input_tested = false, homepod_tested = false })); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
