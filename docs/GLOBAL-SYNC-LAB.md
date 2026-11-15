<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# 全局画面补偿实验
2026-11-15。开发验证工具，尚不代表Windows全局同步已经验收，也不改变正式版启动入口。

## 本批实现
使用Windows原生WinForms/Win32实现独立实验进程，不修改正式WinUI界面。捕获用户选择的一个显示器，把帧按单调时间戳延后呈现，覆盖该显示器上的普通桌面应用。通过只读/global-sync接口（WinToPod.GlobalSync/1）读取音箱状态和建议延后。旧浏览器接口和遥控播放器支路均已删除。
输入当前为协议缓冲+采集估算+本次全局微调，不是声学实测。仅支持0到5000ms，0ms不启动覆盖。
暂限最大960×540、约15fps，80帧队列及一张显示帧，像素缓存约161MiB上限（不含运行时/GDI开销），不落盘、不联网传画面。
退出键Ctrl+Alt+F8必须注册成功；采集失败、状态失联、显示器变化、UI卡顿超过保护时间时退出覆盖或关闭此实验进程。音频主进程保持独立。开始后固定本次延后值，避免采集估算10ms波动导致反复停止；停止后采用最新估算。手动修改微调值停止实验，需重新点击开始；关闭窗口后微调归零，旧播放器偏移不会迁移。

## 使用
先用本次主程序体验入口连接HomePod，从“设置 → 全局补偿”打开实验窗口，选择显示器并点击开始。组件随完整包位于app/全局补偿，不需要单独下载；重复启动只提示已有窗口，避免叠加覆盖。
只作观看实验：键鼠仍控制实时桌面，显示却有延后；操作前先按Ctrl+Alt+F8退出，避免点击旧画面上的控件。
普通浏览器、本地播放器及其他桌面应用作为验证对象；新版不包含浏览器或本地播放器补偿；历史安装的扩展请自行停用或卸载。
本轮不承诺DRM、HDR、独占全屏、受保护窗口和系统安全桌面可用；不绕过系统捕获限制。
若黑屏、递归画面、闪烁，立即退出并记录显卡/显示模式；不将调用成功当作已排除自身捕获的证据。
这条路径延后显示器画面，不降低HomePod实际音频延迟，不作为游戏、通话或乐器低延迟方案，也不是虚拟声卡实现。
GDI原型用来验证跨应用覆盖及操作代价；若机制有效，再评估Windows.Graphics.Capture与GPU/编码缓冲方案提升画质帧率。正式实现尚未定型。

## 检查
dotnet run --project tests/WinToPod.GlobalSyncChecks -c Release
覆盖不提前出帧、精确时间边界、过期帧丢弃、内存帧数上限、释放所有权、重置与时间倒退。合成帧测试不覆盖Windows捕获/覆盖效果或真实音画同步。
CI的preview任务同时编译主程序与实验组件并打包；接口和帧队列检查已加入Windows/Ubuntu核心工作流。运行记录及对应提交需在交付时核对。

## 真机验收
1. 同一显示器分别运行普通网页、本地播放器和桌面动画，观察无扩展时是否确实延后；记录黑屏/递归、边缘和缩放。
2. Ctrl+Alt+F8、主程序退出、音箱断开、显示器切换均应恢复实时桌面；重新开始不残留旧帧。
3. 用有声光标记的测试源同时记录画面与HomePod声音，比较启用前后偏差；界面建议延后不作为测量值。
4. 单列内存、CPU、实际帧率、分辨率与操作影响；未通过前不提升为默认开启能力。

## 媒体信息实测
主程序仍需--media-info；summary.json新增MediaInfoEnabled和ClockAlignedEnabled，避免只看V1.1版本号误判启用项。
日志仅记录无播放会话、多源、非音乐、缺标题、读取中切换、是否有封面等状态，不记录曲名、作者、应用标识或封面。
依次验证单播放器切歌/封面/拖动进度、暂停/退出、多来源、视频切换。分别记录Windows来源、能力门控发送、HomePod实际显示与清空；日志“交给发送链路”不等于接收端已展示。
灯效仅在确认型号及系统支持后验证；通用曲目元数据不是27.2独有协议。

## 官方依据
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
- https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-stretchblt
- https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture
