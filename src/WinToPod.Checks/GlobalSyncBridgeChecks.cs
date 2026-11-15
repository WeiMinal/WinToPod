// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinToPod.Core;

internal static class GlobalSyncBridgeChecks
{
    public static async Task<int> Run()
    {
        var results = new List<string>();
        void Check(string name, bool value) { if (!value) throw new Exception(name); results.Add(name); }
        try
        {
            Check("44100Hz timing and signed correction", GlobalSyncState.FromFrames(88200) == 2000 && GlobalSyncState.FromFrames(88200, -150) == 1850 && GlobalSyncState.FromFrames(99999999) == 5000);
            var estimate = new CaptureDelayEstimator(); estimate.Observe(4410, 0);
            Check("capture queue includes buffered samples", estimate.DelayMs == 100);
            for (var i = 0; i < 40; i++) estimate.Observe(2205, 50);
            Check("draining capture queue retains original sample age", estimate.DelayMs == 100);
            estimate.Observe(0, 500); estimate.Observe(1, double.NaN);
            Check("empty or invalid capture data cannot invent latency", estimate.DelayMs == 100);
            using var bridge = new GlobalSyncBridge(0);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var url = $"http://127.0.0.1:{bridge.Port}/global-sync";
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url)))
                Check("offline by default", !json.RootElement.GetProperty("connected").GetBoolean() && json.RootElement.GetProperty("delayMs").GetInt32() == 0);
            bridge.Update(new(true, 2010));
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url)))
                Check("atomic read-only protocol state", json.RootElement.GetProperty("protocol").GetString() == "WinToPod.GlobalSync/1" && json.RootElement.GetProperty("delayMs").GetInt32() == 2010 && json.RootElement.EnumerateObject().Count() == 3);
            using (var response = await http.PostAsync(url, new StringContent("{}"))) Check("commands rejected", response.StatusCode == HttpStatusCode.BadRequest);
            foreach (var length in new[] { 2, 128, 768 })
            {
                using var response = await http.PostAsync(url, new StringContent(new string('x', length)));
                Check($"rejected {length}-byte body returns complete JSON", response.StatusCode == HttpStatusCode.BadRequest && await response.Content.ReadAsStringAsync() == "{}");
            }
            using (var client = new TcpClient())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await client.ConnectAsync(IPAddress.Loopback, bridge.Port, timeout.Token);
                using var stream = client.GetStream();
                // Split the body across writes to prove the server consumes it before closing.
                // A single write can hide this bug on Linux while Windows sends a connection reset.
                var request = Encoding.ASCII.GetBytes($"POST /global-sync HTTP/1.1\r\nHost: 127.0.0.1:{bridge.Port}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{");
                await stream.WriteAsync(request, timeout.Token);
                var firstByte = new byte[1];
                var pending = stream.ReadAsync(firstByte, timeout.Token).AsTask();
                var early = await Task.WhenAny(pending, Task.Delay(150, timeout.Token)) == pending;
                await stream.WriteAsync("}"u8.ToArray(), timeout.Token);
                using var response = new MemoryStream();
                var count = await pending;
                if (count > 0) response.Write(firstByte, 0, count);
                await stream.CopyToAsync(response, timeout.Token);
                var text = Encoding.ASCII.GetString(response.ToArray());
                Check("rejected fragmented body is consumed before responding", !early);
                Check("rejected fragmented request closes with complete response", text.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal) && text.EndsWith("\r\n\r\n{}", StringComparison.Ordinal));
                Check("rejected bodies leave bridge online", (await http.GetStringAsync(url)).Contains("WinToPod.GlobalSync/1", StringComparison.Ordinal));
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Host = "attacker.example";
                using var response = await http.SendAsync(request); Check("DNS rebinding host rejected", response.StatusCode == HttpStatusCode.BadRequest);
            }
            foreach (var origin in new[] { "https://example.com", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "null" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Add("Origin", origin);
                using var response = await http.SendAsync(request);
                Check("browser origin rejected without CORS: " + origin,
                    response.StatusCode == HttpStatusCode.BadRequest && !response.Headers.Contains("Access-Control-Allow-Origin"));
            }
            foreach (var retiredPath in new[] { "/video-sync", "/video-sync/companion", "/global-sync/companion" })
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{bridge.Port}" + retiredPath);
                Check("retired endpoint unavailable: " + retiredPath, response.StatusCode == HttpStatusCode.BadRequest);
            }
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, bridge.Port);
                using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /global-sync HTTP/1.1\r\nHost: 127.0.0.1:{bridge.Port}\r\nHost: other.example\r\n\r\n"));
                using var reader = new StreamReader(stream);
                Check("duplicate hosts rejected", (await reader.ReadToEndAsync()).StartsWith("HTTP/1.1 400", StringComparison.Ordinal));
            }
            bridge.Update(new(true, int.MaxValue));
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url)))
                Check("global delay bounded", json.RootElement.GetProperty("delayMs").GetInt32() == 5000);
            bridge.Update(GlobalSyncState.Offline);
            using (var json = JsonDocument.Parse(await http.GetStringAsync(url))) Check("disconnect clears global state", !json.RootElement.GetProperty("connected").GetBoolean());
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = results, desktop_capture_tested = false, acoustic_sync_tested = false })); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
