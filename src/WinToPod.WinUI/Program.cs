// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using WinToPod.Desktop;
namespace WinToPod.WinUI;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupDiagnostics.Begin(args);
        try
        {
            NativeWindow.SetIdentity();
            using var instance = new Mutex(true, @"Local\WinToPod.Desktop", out var created);
            if (!created)
            {
                StartupDiagnostics.WindowReady();
                NativeWindow.MessageBox(IntPtr.Zero, "WinToPod 已在运行。请先从托盘退出已有程序，再打开WinToPod。", "WinToPod", 0x40); return;
            }
            StartupDiagnostics.Stage("初始化 WinRT");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callback =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new WinToPodApplication(args);
            });
        }
        catch (Exception e) { StartupDiagnostics.Fail(e); }
    }
}
public sealed partial class WinToPodApplication : Application, IXamlMetadataProvider
{
    private readonly string[] _args;
    private MainWindow? _window;
    private XamlControlsXamlMetaDataProvider? _metadata;
    private XamlControlsXamlMetaDataProvider Metadata => _metadata ??= new();
    public IXamlType GetXamlType(Type type) => Metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name) => Metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => Metadata.GetXmlnsDefinitions();
    public WinToPodApplication(string[] args)
    {
        _args = args;
        UnhandledException += (_, e) => { if (!StartupDiagnostics.Ready) { e.Handled = true; StartupDiagnostics.Fail(e.Exception); Exit(); } else StartupDiagnostics.RecordFailure(e.Exception); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupDiagnostics.Stage("加载 WinUI 控件资源"); Resources.MergedDictionaries.Add(new XamlControlsResources());
            var selfCheck = Array.IndexOf(_args, "--self-check");
            var path = selfCheck >= 0 && selfCheck + 1 < _args.Length ? _args[selfCheck + 1] : null;
            var preview = path is not null || _args.Contains("--preview");
            StartupDiagnostics.Stage("创建窗口");
            _window = new MainWindow(preview ? new DesktopSettings() : DesktopSettings.Load(), _args.Contains("--startup"), preview, path); _window.Activate();
        }
        catch (Exception e) { StartupDiagnostics.Fail(e); Exit(); }
    }
}
