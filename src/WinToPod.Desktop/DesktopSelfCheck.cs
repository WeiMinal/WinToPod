// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Windows.Forms.Integration;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using WinToPod.AirPlay.Discovery;

namespace WinToPod.Desktop;

internal static class DesktopSelfCheck
{
    // Windows-only. Does not scan, capture, connect, write real settings or register startup.
    public static void Run(string outputPath)
    {
        using var form = new MainForm(new DesktopSettings(), false, enableVideoBridge: false, enableAutomation: false);
        _ = form.Handle; form.PerformLayout();
        if (form.Controls.OfType<ElementHost>().SingleOrDefault()?.Child is not C.Border { Child: FluentDeviceView })
            throw new InvalidOperationException("Main page must use the actual Fluent WPF view.");
        if (W.Application.Current.TryFindResource("AccentButtonStyle") is not W.Style)
            throw new InvalidOperationException("The official Fluent theme was not loaded.");
        var connections = 0; var volumeCalls = 0;
        var view = new FluentDeviceView(_ => { connections++; return Task.CompletedTask; }, _ => { }, () => Task.CompletedTask, () => { }, _ => volumeCalls++, () => Task.CompletedTask);
        var endpoint = new ReceiverEndpoint(ReceiverDiscovery.AirPlayService, "fixture", "fixture.local.", 7000, "127.0.0.1", ["127.0.0.2"]);
        AirPlayReceiver Device(string id, string model, bool pod, string? group = null) => new(id, id, model, pod, [endpoint], new Dictionary<string, string?>(), group, null);
        var fixtureWhite = Device("white", "AudioAccessory5,1", true, "pair"); var fixtureBlack = Device("black", "AudioAccessory5,1", true, "pair");
        var tv = Device("tv", "AppleTV6,2", false); var mac = Device("mac", "Macmini9,1", false);
        var entries = new[] { new PlaybackEntry("one", "客厅的一对音箱", "HomePod 组合 · 2只", [fixtureWhite, fixtureBlack], fixtureWhite), new PlaybackEntry("two", "Apple TV", "Apple TV", [tv], tv), new PlaybackEntry("three", "Mac mini", "Mac", [mac], mac) };
        DeviceViewState State(bool working = false, bool connected = false, bool single = false) => new(entries, "one", working ? "one" : null, false, working, connected, true, single, "连接音箱", "需要使用时，点击设备旁的连接。", false, 37);
        foreach (var (width, height) in new[] { (544d, 430d), (608d, 480d), (632d, 542d), (712d, 600d), (1032d, 760d) })
        {
            view.Render(State()); view.Measure(new W.Size(width, height)); view.Arrange(new W.Rect(0, 0, width, height)); view.UpdateLayout();
            var position = view.VolumeRegion.TranslatePoint(new W.Point(0, 0), view);
            if (position.Y < 0 || position.Y + view.VolumeRegion.ActualHeight > height + .5)
                throw new InvalidOperationException("Volume region escaped the resizable layout.");
            var before = view.DeviceList.RenderSize; view.Render(State(single: true)); view.UpdateLayout();
            if (view.DeviceList.RenderSize != before) throw new InvalidOperationException("Selecting group fallback changed the device viewport.");
        }
        if (Descendants(view).OfType<C.Image>().Any(i => i.Source is System.Windows.Media.Imaging.BitmapSource))
            throw new InvalidOperationException("Homepage duplicates the shell logo.");
        view.Render(State(working: true, connected: true));
        var indicators = Descendants(view).OfType<System.Windows.Documents.InlineUIContainer>().ToArray();
        if (indicators.Length != 3 || indicators.Any(i => i.BaselineAlignment != W.BaselineAlignment.Center))
            throw new InvalidOperationException("Connection indicators must align inside the device text line.");
        var list = (C.ListBox)view.DeviceList;
        view.Render(State()); view.Measure(new W.Size(608, 528)); view.Arrange(new W.Rect(0, 0, 608, 528)); view.UpdateLayout();
        var lastRow = (C.ListBoxItem)list.Items[2];
        if (lastRow.TranslatePoint(new W.Point(0, 0), list).Y + lastRow.ActualHeight > list.ActualHeight + .5)
            throw new InvalidOperationException("Default viewport does not fit three device rows.");
        var first = (C.ListBoxItem)list.Items[0];
        var connect = Descendants(first).OfType<C.Button>().First(b => b.Content as string == "连接");
        if (!connect.IsEnabled) throw new InvalidOperationException("Discovered online target is not clickable.");
        connect.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        if (connections != 1 || volumeCalls != 0) throw new InvalidOperationException("Inline connection or render-only volume behavior changed.");
        view.Render(State(working: true, connected: true));
        if (((C.ListBoxItem)list.Items[1]).IsEnabled) throw new InvalidOperationException("Another target remained selectable during playback.");
        if (Descendants(view).OfType<C.Button>().Any(b => b.Content as string == "⋯") || Descendants(view).OfType<C.TextBlock>().Any(t => t.Text is "视频同步" or "连接详情与诊断")) throw new InvalidOperationException("Homepage diagnostic entry remains.");
        if (Descendants(view).OfType<C.TextBlock>().Any(t => !t.FontFamily.Source.Contains("Microsoft YaHei UI", StringComparison.Ordinal)))
            throw new InvalidOperationException("Chinese UI font fallback was not declared.");
        using var password = new PasswordDialog("测试音箱", false, true);
        var passwordRoot = password.Controls.OfType<ElementHost>().Single().Child;
        if (Descendants(passwordRoot).OfType<C.PasswordBox>().Count() != 1 || !password.RememberPlayback)
            throw new InvalidOperationException("Password masking or remembered choice missing.");
        var detailClicks = 0; var exportClicks = 0;
        using var settings = new SettingsDialog(false, false, true, true, toggleSoundTest: () => { }, showDiagnostics: _ => detailClicks++, exportDiagnostics: _ => exportClicks++);
        _ = settings.Handle; settings.Show(); settings.PerformLayout(); Application.DoEvents();
        var nativeControls = NativeDescendants(settings).ToArray();
        var tabs = nativeControls.OfType<TabControl>().Single();
        if (!tabs.TabPages.Cast<TabPage>().Select(t => t.Text).SequenceEqual(new[] { "常规", "音画补偿", "诊断", "关于" }) || nativeControls.OfType<RadioButton>().Count() != 2 || settings.Policy != WinToPod.Core.ConnectionPolicy.Conservative || !settings.AutoConnectOnStart)
            throw new InvalidOperationException("Native property-sheet tabs, modes or defaults are missing.");
        if (Math.Abs(settings.Font.SizeInPoints - 9) > .01 || nativeControls.OfType<ScrollableControl>().Any(c => c.AutoScroll || c.VerticalScroll.Visible))
            throw new InvalidOperationException("Settings font or scrollbar differs from the native compact layout.");
        if (tabs.TabPages[0].Controls.OfType<CompactColumn>().Single().Controls[0].Name != "OwnershipSettings")
            throw new InvalidOperationException("Ownership must lead general settings.");
        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page; settings.PerformLayout(); page.PerformLayout();
            var content = page.Controls[0];
            foreach (var column in NativeDescendants(page).OfType<CompactColumn>())
            {
                var previous = column.Padding.Top;
                foreach (Control child in column.Controls)
                {
                    if (!child.Visible) continue;
                    if (child.Dock != DockStyle.None || child.Top < previous || child.Bottom > column.ClientSize.Height - column.Padding.Bottom + 1)
                        throw new InvalidOperationException("Native row overlap/cropping: " + page.Text + " / " + child.GetType().Name);
                    previous = child.Bottom;
                    if (child is Label label)
                    {
                        var measured = TextRenderer.MeasureText(label.Text.Length == 0 ? " " : label.Text, label.Font,
                            new Size(Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal - (int)Math.Ceiling(2 * label.DeviceDpi / 96d)), int.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                        if (measured.Height + label.Padding.Vertical > label.ClientSize.Height) throw new InvalidOperationException("Wrapped label was clipped: " + page.Text);
                    }
                }
            }
            if (content.Bottom > page.ClientSize.Height - page.Padding.Bottom + 1 || content.Right > page.ClientSize.Width - page.Padding.Right + 1)
                throw new InvalidOperationException("Native settings page overflows: " + page.Text);
        }
        foreach (var button in Descendants(view).OfType<C.Button>())
            if (button.FocusVisualStyle is not null) throw new InvalidOperationException("External button focus frame remains.");
        tabs.SelectedIndex = 2;
        nativeControls.OfType<Button>().Single(b => b.Name == "ShowDiagnosticsAction").PerformClick();
        nativeControls.OfType<Button>().Single(b => b.Name == "ExportDiagnosticsAction").PerformClick();
        if (detailClicks != 1 || exportClicks != 1 || settings.DialogResult != DialogResult.None) throw new InvalidOperationException("Settings diagnostics action missing or closed settings.");
        var test = nativeControls.OfType<Button>().Single(b => b.Name == "TestSoundAction");
        if (!test.Enabled) throw new InvalidOperationException("Connected sound check is unavailable.");
        test.PerformClick();
        if (settings.DialogResult != DialogResult.None) throw new InvalidOperationException("Sound check closed settings.");
        if (Descendants(view).OfType<C.TextBlock>().Any(t => t.Text.Contains("2只", StringComparison.Ordinal) || t.Text == "已连接")) throw new InvalidOperationException("Homepage still shows group count or connected text.");
        var mutes = 0; var sources = 0;
        var soundView = new FluentDeviceView(_ => Task.CompletedTask, _ => { }, () => Task.CompletedTask, () => { }, _ => { }, () => Task.CompletedTask, () => mutes++, () => sources++);
        soundView.Render(State(working: true, connected: true));
        Descendants(soundView).OfType<C.Button>().Single(b => b.Name == "MuteAction").RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        Descendants(soundView).OfType<C.Button>().Single(b => b.Name == "CaptureSourceAction").RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        if (mutes != 1 || sources != 1) throw new InvalidOperationException("Homepage sound actions are not wired.");
        using var details = new DetailsDialog("fixture", () => { });
        var detailsHost = details.Controls.OfType<ElementHost>().Single();
        if (detailsHost.Child is not C.Border { Child: C.DockPanel detailsRoot } || detailsRoot.Margin != new W.Thickness(24))
            throw new InvalidOperationException("ElementHost erased diagnostic window content margins.");
        using var expectedBrand = BrandAssets.WindowIcon();
        if (form.Icon is null || !SameIcon(form.Icon, expectedBrand))
            throw new InvalidOperationException("Approved brand icon is missing.");
        var temporaryPairings = Path.Combine(Path.GetTempPath(), "WinToPod-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string controller = "CA58F410-91D9-4C29-A81E-998EC4B029C1";
            byte[] seed;
            using (var store = new DesktopPairingStore(temporaryPairings))
            {
                seed = store.GetControllerSecret(controller);
                using var credential = new WinToPod.AirPlay.Pairing.PairingCredentials(controller, seed, "test-accessory"u8, new byte[32]);
                store.Save("fixture-device", credential);
            }
            using (var store = new DesktopPairingStore(temporaryPairings))
            using (var restoredPairing = store.Load("fixture-device", controller))
                if (restoredPairing is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(restoredPairing.ControllerSecret, seed))
                    throw new InvalidOperationException("Current-user DPAPI pairing storage did not round-trip.");
            var scope = new string('A', 64);
            using (var store = new DesktopPlaybackCredentialStore(temporaryPairings))
            using (var proof = new WinToPod.AirPlay.Transport.PlaybackCredential(scope, "MD5", new byte[16]))
            {
                store.SetRemember("white", true); store.SetRemember("black", true);
                store.Save("white", proof); store.Save("black", proof);
                store.SetRemember("memory-only", false); store.Save("memory-only", proof);
            }
            using (var store = new DesktopPlaybackCredentialStore(temporaryPairings))
            {
                using var white = store.Load("white", scope); using var black = store.Load("black", scope); using var ephemeral = store.Load("memory-only", scope);
                if (white is null || black is null || ephemeral is not null) throw new InvalidOperationException("Playback credential persistence selection did not round-trip.");
                store.Forget("white");
            }
            using (var store = new DesktopPlaybackCredentialStore(temporaryPairings))
            using (var white = store.Load("white", scope))
            using (var black = store.Load("black", scope))
            using (var pairing = new DesktopPairingStore(temporaryPairings))
            using (var identity = pairing.Load("fixture-device", controller))
                if (white is not null || black is null || identity is null) throw new InvalidOperationException("Forgetting playback must preserve other speakers and pairing identity.");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
            var path = Path.Combine(temporaryPairings, "paired-receivers.dat");
            var corrupted = File.ReadAllBytes(path); corrupted[^1] ^= 1; File.WriteAllBytes(path, corrupted);
            using var invalidStore = new DesktopPairingStore(temporaryPairings);
            try { using var invalid = invalidStore.Load("fixture-device", controller); throw new InvalidOperationException("Corrupted pairing storage was accepted."); }
            catch (IOException) { }
        }
        finally { if (Directory.Exists(temporaryPairings)) Directory.Delete(temporaryPairings, true); }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(new { passed = true, official_fluent_loaded = true, layout_dip_sizes = new[] { "544x430", "608x480", "632x542", "712x600", "1032x760" }, inline_connect = true, no_homepage_video_link = true, settings_tabs = 4, native_property_sheet_controls = true, settings_page_scrollbars = false, ownership_modes = 2, diagnostic_margin_preserved = true, pairing_and_playback_dpapi = true, network_started = false, audio_captured = false, actual_multi_monitor_dpi_tested = false }));
    }
    private static IEnumerable<Control> NativeDescendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls) foreach (var item in NativeDescendants(child)) yield return item;
    }
    private static bool SameIcon(Icon a, Icon b)
    {
        using var left = a.ToBitmap(); using var right = b.ToBitmap();
        if (left.Size != right.Size) return false;
        for (var y = 0; y < left.Height; y++) for (var x = 0; x < left.Width; x++) if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
        return true;
    }
    private static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject parent)
    {
        yield return parent;
        foreach (var child in W.LogicalTreeHelper.GetChildren(parent).OfType<W.DependencyObject>()) foreach (var node in Descendants(child)) yield return node;
    }
}
