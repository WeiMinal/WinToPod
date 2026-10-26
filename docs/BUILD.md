# 构建与检查

## Windows

安装 .NET 10 SDK、Python 3.11+、PowerShell 7，以及 Visual Studio C++ x64 构建工具和 Windows SDK：

```powershell
pwsh -File tools/Build.ps1 -WindowsSelfCheck
python tools/CheckSource.py
python tools/Package.py
```

构建入口为 `src/WinToPod.WinUI`；`src/WinToPod.Desktop` 仅保留共享 Windows 服务和品牌素材，旧 WPF/WinForms 前端已移除。`packaging/launcher.c` 从源码构建根目录启动器，没有预编译启动器输入。

`artifacts/win-x64` 是每次重新创建的便携目录，根目录只保留 `WinToPod.exe`、`app` 和使用说明；`artifacts/release` 包含运行包、对应源码和 SHA256。`artifacts/validation` 存放实际检查报告，打包不采纳旧版验证结论。

`-WindowsSelfCheck` 从根目录启动器启动合成界面，验证 WinUI 资源加载与可见布局，不搜索或连接音箱，不采集系统声音。真实采集、DPAPI、任务栏主题、弹窗置顶和多屏 DPI 仍需 Windows 实机验证。

GitHub 工作流在推送/PR 时构建、检查并上传产物。main 推送更改产品版本后，检查成功才创建对应 GitHub Release；标题和四条说明取自 `docs/RELEASE.md`。发布先创建草稿、上传文件，再公开，失败不会公开缺少附件的 Release。V1.0 历史标签与附件不变。

## 协议回归

```sh
python -m pip install -r tests/requirements.txt
dotnet build src/WinToPod.Checks -c Release -m:1
python tests/receiver_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
python tests/native_pair_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
python tests/playback_digest_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
python tests/stability_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
```

按顺序执行，模拟器共享回环 PTP 端口 31319/31320。生产端口仍为 319/320。稳定性默认约 11 分钟，缩短执行不能算长期验证。V1.1 云端完整推流模拟存在 PTP/调度保护触发，不能视为全部通过，见 [验证范围](TESTING.md)。

## 可选检查

```sh
npm install --prefix tests
node tests/video_sync_companion.mjs
npx --prefix tests playwright install chromium
node tests/video_sync_browser.mjs
```

浏览器测试使用本机生成视频，不登录真实账号。图标重建使用 `tools/BuildBrand.py` 和 `tools/requirements.txt`，无需重新生成设计。

Linux 可交叉编译 WinUI 和 C 启动器，但不能运行 Windows 界面。Windows App SDK 的 Windows 资源工具需要适配；正式 CI 使用 Windows 原生工具链。不得用交叉编译或替代边界测试冒充 Windows/HomePod 实测。
