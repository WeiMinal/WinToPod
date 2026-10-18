# 构建与检查

## Windows

安装 .NET 10 SDK、Python 3.11+ 和 PowerShell 7。便携包构建不需要 Python 音频模块或浏览器测试环境：

```powershell
pwsh -File tools/Build.ps1
python tools/CheckSource.py
python tools/Package.py
```

`Build.ps1 -WindowsSelfCheck` 会额外打开合成界面，检查布局与临时目录中的 DPAPI 往返；不扫描、连接音箱，不采集系统声音，不修改真实配置或注册启动项。

`artifacts/win-x64` 是自包含发布目录；`artifacts/release` 是交付 ZIP、源码 ZIP 和 SHA256。构建及检查输出放在 `artifacts/validation`，均排除在公开源码之外。发布不自动执行 Git commit、tag、push 或 GitHub Release。

## 协议回归

```sh
python -m pip install -r tests/requirements.txt
dotnet build src/WinToPod.Checks -c Release -m:1
python tests/receiver_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
python tests/native_pair_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
python tests/playback_digest_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
python tests/stability_simulator.py dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll
```

按顺序运行，模拟器共享回环 PTP 端口 31319/31320。生产端口仍为 319/320，不访问真实设备。稳定性默认约 11 分钟长播，可通过 `WINTOPOD_SOAK_SECONDS` 调整；缩短后的结果不能描述为长时间稳定验证。

## 可选视频检查与图标生成

```sh
npm install --prefix tests
node tests/video_sync_companion.mjs
npx --prefix tests playwright install chromium
node tests/video_sync_browser.mjs
```

实际浏览器测试只播放本机生成的视频，不登录 B 站或访问用户账号。`WINTOPOD_BROWSER` 可指定浏览器可执行文件；`WINTOPOD_VALIDATION_DIRECTORY` 可指定输出目录。验证通过不等于真实 HomePod 声学同步已验证。

重建图标：`python -m pip install -r tools/requirements.txt`，然后运行 `python tools/BuildBrand.py`。只使用仓库内的透明源 PNG，不需要联网生成或重新设计。

## Linux 交叉编译

可使用相同 SDK 构建基础检查，再执行：

```sh
dotnet publish src/WinToPod.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:RuntimeFrameworkVersion=10.0.13 -p:DebugType=None -p:DebugSymbols=false -m:1 -o artifacts/win-x64
python tools/CheckSource.py
python tools/Package.py
```

这只能证明可构建及覆盖的离线规则通过，不能代替 Windows 启动、原生采集、界面/DPI、DPAPI 或实际音箱验证。随附 Windows CI 配置尚需首次推送后实际执行。
