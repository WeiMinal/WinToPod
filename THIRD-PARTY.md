# WinToPod第三方组件

AirPlay发现、SRP会话流程、RTSP、plist、RTP、ALAC帧封装、NTP与产品策略由WinToPod独立实现。没有引入AirFlash或其他AirPlay发送端作为源码基础/运行时引擎。

| 组件 | 版本 | 用途 | 许可 |
| --- | --- | --- | --- |
| Microsoft .NET运行时 | 10.0.13 | 自包含Windows桌面执行环境 | MIT等，随附原始许可/第三方声明 |
| Microsoft Windows App SDK | 2.5.1 | 原生 WinUI 3 界面、窗口与通知能力 | 随附 SDK 原始许可与第三方声明 |
| BouncyCastle.Cryptography | 2.7.0 | ChaCha20-Poly1305托管回退、完整配对的Ed25519与X25519密码原语 | BouncyCastle MIT基础许可，见licenses |
| NAudio.Wasapi / NAudio.Core | 2.2.1 | Windows输出设备枚举与WASAPI系统声音采集 | MIT，见licenses |
| System.Security.Cryptography.ProtectedData | 10.0.0 | Windows当前用户DPAPI加密保存配对身份，密码本身不保存 | MIT，见licenses/DOTNET-LICENSE.txt |

原始许可证保存于licenses/，发布包随附同一份文件。WinToPod自身源代码沿用GPL-3.0-or-later。

Python srptools、cryptography、标准库plistlib和FFmpeg仅用于独立交叉检查，不是软件运行时的外部音频发送引擎。固定密钥、口令与SRP私有值均为合成测试数据，不属于用户凭据。

界面使用 Microsoft Windows App SDK 的 WinUI 3 原生控件，旧 WPF/Windows Forms 前端已移除。未额外引入第三方Fluent控件库，未打包字体文件、Apple官方图片或SF Symbols。

应用采用睡猫 Logo，透明源图及应用尺寸见 src/WinToPod.Desktop/Assets/Brand/SOURCE.md。设备图标采用 Lucide 原始 SVG，完整 ISC 及 Feather 衍生 MIT 声明见 licenses/Lucide-LICENSE.txt。代码许可不等于商标授权，尚未执行完整商标查重。

Playwright、pngjs和Chromium只用于开发检查，不随Windows 程序分发；测试视频由FFmpeg合成。浏览器助手为WinToPod独立代码，未复制其他音画延迟扩展的实现。

Lucide设备图标包括speaker、laptop-minimal、tv-minimal和cast，固定修订、原文件SHA256见src/WinToPod.Desktop/Assets/DeviceIcons/SOURCE.md。组合为两个speaker并排。Windows Forms 框架许可仍随附于 licenses/WinForms-LICENSE.txt。

Windows App SDK 组件许可和 NOTICE 以 licenses/WindowsAppSDK/ 下随附原件为准。
