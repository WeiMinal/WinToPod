using WinToPod.Desktop;
var results = new List<string>();
void Check(string name, Action body) { body(); results.Add(name); }
void Assert(bool ok) { if (!ok) throw new Exception("routing check failed"); }
Check("switches media roles while preserving communication role", () => {
    var state = new[] { "a", "b", "calls" }; var writes = new List<int>();
    OutputSwitchTransaction.Apply("new", role => state[role], (role, id) => { writes.Add(role); state[role] = id; });
    Assert(state.SequenceEqual(new[] { "new", "new", "calls" }) && writes.SequenceEqual(new[] { 0, 1 }));
});
Check("unchanged output does not write", () => OutputSwitchTransaction.Apply("same", _ => "same", (_, _) => throw new Exception("unexpected write")));
Check("partial failure rolls back previously changed roles", () => {
    var state = new[] { "a", "b" }; bool caught = false;
    try { OutputSwitchTransaction.Apply("new", role => state[role], (role, id) => { if (role == 1 && id == "new") throw new IOException("failed"); state[role] = id; }); } catch (IOException) { caught = true; }
    Assert(caught && state.SequenceEqual(new[] { "a", "b" }));
});
Check("readback mismatch cannot report success", () => {
    var state = new[] { "a", "b" }; bool caught = false;
    try { OutputSwitchTransaction.Apply("new", role => state[role], (role, id) => { if (role == 0) state[role] = id; }); } catch (IOException) { caught = true; }
    Assert(caught && state.SequenceEqual(new[] { "a", "b" }));
});
Check("rollback does not overwrite a concurrent external change", () => {
    var state = new[] { "a", "b" };
    try { OutputSwitchTransaction.Apply("new", role => state[role], (role, id) => { if (role == 1) { state[0] = "external"; throw new IOException(); } state[role] = id; }); } catch (IOException) { }
    Assert(state.SequenceEqual(new[] { "external", "b" }));
});
Check("rollback failure is explicit", () => {
    var state = new[] { "a", "b" }; bool caught = false;
    try { OutputSwitchTransaction.Apply("new", role => state[role], (role, id) => { if (role == 1 || id == "a") throw new IOException(); state[role] = id; }); } catch (IOException e) { caught = e.Message.Contains("恢复"); }
    Assert(caught);
});
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = true, checks = results, actual_windows_default_switch_tested = false }));
