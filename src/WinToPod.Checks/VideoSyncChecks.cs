// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinToPod.Core;

internal static class VideoSyncChecks
{
    public static async Task<int> Run()
    {
        var results = new List<string>();
        void Check(string name, bool value) { if (!value) throw new Exception(name); results.Add(name); }
        try
        {
            Check("44100Hz timing and signed correction", VideoSyncState.FromFrames(88200) == 2000 && VideoSyncState.FromFrames(88200, -150) == 1850 && VideoSyncState.FromFrames(99999999) == 5000);
            var media = Path.Combine(Path.GetTempPath(), "a space & $(literal).mp4");
            var vlc = VideoPlayerArguments.Create("vlc.exe", media, 2000, "", 12345, "unit-test-only");
            Check("VLC negative audio offset and isolated process", vlc.Contains("--audio-desync=-2000") && vlc.Contains("--no-one-instance") && vlc.Contains("--http-host=127.0.0.1") && vlc[^1] == media);
            var mpv = VideoPlayerArguments.Create("mpv.exe", media, 1850, "pipe", 0, "");
            Check("mpv signed fractional offset and literal path", mpv.Contains("--audio-delay=-1.85") && mpv[^2] == "--" && mpv[^1] == media);
            try { VideoPlayerArguments.Create("unknown.exe", media, 20, "", 0, ""); throw new Exception("Untrusted player accepted"); } catch (ArgumentException) { results.Add("unknown player rejected"); }
            var estimate = new CaptureDelayEstimator(); estimate.Observe(4410, 0);
            Check("capture queue includes buffered samples", estimate.DelayMs == 100);
            for (var i = 0; i < 40; i++) estimate.Observe(2205, 50);
            Check("draining capture queue retains original sample age", estimate.DelayMs == 100);
            estimate.Observe(0, 500); estimate.Observe(1, double.NaN);
            Check("empty or invalid capture data cannot invent latency", estimate.DelayMs == 100);
            using var bridge = new VideoSyncBridge(0);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var url = $"http://127.0.0.1:{bridge.Port}/video-sync";
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url)))
                Check("offline by default", !json.RootElement.GetProperty("connected").GetBoolean() && json.RootElement.GetProperty("delayMs").GetInt32() == 0);
            bridge.Update(new(true, 2010));
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url)))
                Check("atomic read-only protocol state", json.RootElement.GetProperty("protocol").GetString() == "WinToPod.VideoSync/1" && json.RootElement.GetProperty("delayMs").GetInt32() == 2010 && json.RootElement.EnumerateObject().Count() == 3);
            Check("audio connected does not claim browser frames exist", !bridge.CompanionStatus.Detected);
            using (var response = await http.PostAsync(url, new StringContent("{}"))) Check("commands rejected", response.StatusCode == HttpStatusCode.BadRequest);
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Host = "attacker.example";
                using var response = await http.SendAsync(request); Check("DNS rebinding host rejected", response.StatusCode == HttpStatusCode.BadRequest);
            }
            foreach (var (origin, allowed) in new[] { ("https://example.com", false), ("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true) })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Add("Origin", origin);
                using var response = await http.SendAsync(request); Check("CORS " + origin, response.Headers.Contains("Access-Control-Allow-Origin") == allowed);
            }
            const string report = "{\"phase\":\"active\",\"reason\":\"none\",\"framesPresented\":12,\"observedDelayMs\":2013}";
            async Task<HttpStatusCode> Report(string payload, bool origin = true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url + "/companion") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                if (origin) request.Headers.Add("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                using var response = await http.SendAsync(request); return response.StatusCode;
            }
            Check("ordinary callers cannot submit frame telemetry", await Report(report, false) == HttpStatusCode.BadRequest && !bridge.CompanionStatus.Detected);
            Check("extension heartbeat records actual presentation telemetry", await Report(report) == HttpStatusCode.OK && bridge.CompanionStatus is { Detected: true, Phase: "active", FramesPresented: 12, ObservedDelayMs: 2013 });
            Check("unknown telemetry fields rejected", await Report(report[..^1] + ",\"url\":\"https://example.com\"}") == HttpStatusCode.BadRequest);
            Check("malformed telemetry rejected without stopping server", await Report("{}") == HttpStatusCode.BadRequest && await http.GetStringAsync(url) is { Length: > 0 });
            bridge.Update(VideoSyncState.Offline);
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url))) Check("disconnect releases companion", !json.RootElement.GetProperty("connected").GetBoolean());
            await Task.Delay(5100);
            Check("stale browser heartbeat expires", !bridge.CompanionStatus.Detected);
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = results, browser_execution_tested = false, windows_player_execution_tested = false })); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
