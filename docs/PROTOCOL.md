# 协议研究参考

AirPlay 2 缺少可供本项目直接遵循的完整官方公开发送端规范。以下资料用于研究可观察行为与接口，WinToPod 独立实现发现、配对、控制、音频封装和成员协调；外部项目的成功不等于本项目已验证。

- [Unofficial AirPlay Protocol Specification](https://openairplay.github.io/airplay-spec/)：公开协议字段与音频/配对信息。
- [tuxplay 协议观察](https://github.com/ksha23/tuxplay/blob/main/docs/airplay2-protocol.md)：实时模式与原生 HomePod 对的公开实测记录。
- [airplay2-sender-cpp](https://github.com/akustikrausch/airplay2-sender-cpp)：事件确认等已观察现象的交叉参考。
- [Apple：设置 HomePod 立体声对](https://support.apple.com/guide/homepod/set-up-a-stereo-pair-apd1ed62a52a/homepod)：原生组合的用户侧行为。
- [Microsoft ApplicationLoopback](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopback/)：Windows 进程树采集接口。
- [Microsoft ActivateAudioInterfaceAsync](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync)：原生激活与回调生存期要求。

`AirPlay/999.0.0` 是当前互操作请求配置中的 User-Agent，不是产品版本；产品版本单独记录为 V1.1。静态协议配置不应为了改版本号而随意更换，兼容变更需要接收端回归。

尚未完成完整 PTP 伺服、多组独立目标同步、所有接收端访问策略与所有固件互操作。不要把握手/准备成功等同实际可听，也不要把控制通道 EOF 直接认作被 Apple TV 接管。
