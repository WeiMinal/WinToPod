// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using NAudio.Wave;
using WinToPod.Desktop;

var checks = new List<string>();
async Task Check(string name, short[] values)
{
    var original = new byte[values.Length * 2];
    for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(original.AsSpan(i * 2), values[i]);
    using var source = new LoopbackAudioSource(null);
    var supplied = (byte[])original.Clone(); WasapiLoopbackCapture.Latest.Emit(supplied);
    if (!original.AsSpan().SequenceEqual(supplied)) throw new Exception("Capture modified shared driver buffer.");
    Array.Fill(supplied, (byte)0x77); // The driver may immediately reuse its callback buffer.
    var output = new byte[original.Length]; await source.ReadAsync(output, CancellationToken.None);
    if (!original.AsSpan().SequenceEqual(output)) throw new Exception(name + ": samples changed or channels reordered.");
    var expectedPeak = values.Max(v => Math.Abs(v / 32768d));
    if (Math.Abs(source.Peak - expectedPeak) > 1e-12) throw new Exception(name + ": meter changed signal level.");
    checks.Add(name);
}
await Check("all 65536 signed 16-bit values remain bit exact", Enumerable.Range(short.MinValue, 65536).Select(i => (short)i).ToArray());
await Check("distinct stereo channels retain sign and order", [32767, -32768, 1, -1, 12000, -3000, 0, 0]);
await Check("quiet samples retain single-bit detail", Enumerable.Range(-100, 201).SelectMany(i => new[] { (short)i, (short)-i }).ToArray());
await Check("silence does not receive gain or noise", new short[704]);
var volumeChecks = new List<string>();
void VolumeCheck(string name, bool condition) { if (!condition) throw new Exception(name); volumeChecks.Add(name); }
var changes = new List<int>();
var link = new EndpointVolumeLink("synthetic-capture", changes.Add);
var endpoint = NAudio.CoreAudioApi.MMDevice.Latest.AudioEndpointVolume;
link.SetPercent(0);
VolumeCheck("mute preserves the previous Windows slider level", endpoint.Mute && Math.Abs(endpoint.MasterVolumeLevelScalar - .38f) < .001);
var writes = endpoint.Writes; link.SetPercent(0);
VolumeCheck("repeating mute is idempotent", endpoint.Writes == writes);
link.SetPercent(16);
VolumeCheck("raising volume explicitly unmutes", !endpoint.Mute && Math.Abs(endpoint.MasterVolumeLevelScalar - .16f) < .001);
VolumeCheck("self-originated Windows notifications do not feed back", changes.Count == 0);
endpoint.External(.45f, false); endpoint.External(.45f, true);
VolumeCheck("external volume and mute changes remain observable", changes.SequenceEqual(new[] { 45, 0 }));
link.Dispose(); writes = endpoint.Writes; link.SetPercent(80); endpoint.External(.50f, false);
VolumeCheck("detached volume link never writes or observes another owner's sound", endpoint.Writes == writes && changes.Count == 2);
var processChecks = new List<string>();
using (var source = await LoopbackAudioSource.OpenAsync(null, CancellationToken.None))
{
    if (source.CaptureMode != "application-mix-pre-master") throw new Exception("Default is not the pre-master mix.");
    var process = ProcessAudioCapture.Instances.Last();
    byte[] frame = [0x34, 0x12, 0xCC, 0xED];
    foreach (var percent in new[] { 1, 8, 16, 20, 50, 100 })
    {
        NAudio.CoreAudioApi.MMDevice.Latest.AudioEndpointVolume.External(percent / 100f, false);
        process.Emit(frame); var output = new byte[4]; await source.ReadAsync(output, CancellationToken.None);
        if (!frame.SequenceEqual(output)) throw new Exception("Source samples followed master volume.");
    }
    processChecks.Add("pre-master application samples stay identical at 1/8/16/20/50/100 percent");
    await source.IncludePlayerAsync(123, CancellationToken.None);
    process.Emit(frame); ProcessAudioCapture.Instances.Last().Emit(new byte[4]); var merged = new byte[4]; await source.ReadAsync(merged, CancellationToken.None);
    if (!frame.SequenceEqual(merged)) throw new Exception("Player inclusion duplicated desktop audio.");
    processChecks.Add("including an app-launched player does not double the desktop mix");
    process.Emit([0, 0, 0, 0]); ProcessAudioCapture.Instances.Last().Emit(frame); await source.ReadAsync(merged, CancellationToken.None);
    if (!frame.SequenceEqual(merged)) throw new Exception("App-launched player is inaudible.");
    processChecks.Add("app-launched player remains audible through a dedicated include-tree stream");
    process.Emit([0xFF, 0x7F, 0, 0x80]); ProcessAudioCapture.Instances.Last().Emit([0xFF, 0x7F, 0, 0x80]); await source.ReadAsync(merged, CancellationToken.None);
    if (!merged.SequenceEqual(new byte[] { 0xFF, 0x7F, 0, 0x80 })) throw new Exception("Independent source mix overflow wrapped.");
    processChecks.Add("independent mix clips safely instead of integer wrap");
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = processChecks, production_audio_source_linked = true, process_boundary_simulated = true, actual_windows_activation_tested = false }));
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = volumeChecks, production_endpoint_link_linked = true, endpoint_boundary_simulated = true, saved_receiver_scale_unchanged = true, hardware_loudness_tested = false }));
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, production_audio_source_linked = true, driver_boundary_simulated = true, actual_wasapi_tested = false, homepod_tested = false, fixed_pcm_gain = 1.0 }));
