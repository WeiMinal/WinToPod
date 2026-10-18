// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;

namespace WinToPod.AirPlay.Media;

public sealed class SessionInterruption(string stage, Exception cause)
    : IOException(MessageFor(stage, cause), cause)
{
    public string Stage { get; } = stage;
    public string Category { get; } = cause is EndOfStreamException ? "remote-eof" : cause is TimeoutException ? "timeout" : cause is SocketException ? "socket" : "protocol";
    public string SafeDiagnostic => $"会话故障：stage={Stage}; kind={Category}; exception={InnerException!.GetType().Name}" +
        (InnerException is SocketException socket ? $"; socket={socket.SocketErrorCode}; native={socket.NativeErrorCode}" : "");
    private static string MessageFor(string stage, Exception cause) => cause switch
    {
        EndOfStreamException => stage == "events" ? "音箱关闭了事件连接，已停止整组播放。" : "音箱关闭了播放控制连接，已停止整组播放。",
        TimeoutException => "音箱控制请求超时，已停止整组播放。",
        SocketException => "音箱网络通道出现错误，已停止整组播放。",
        _ => stage == "clock" ? "音箱同步时钟中断，已停止整组播放。" : "音箱播放通道中断，已停止整组播放。"
    };
    public static SessionInterruption? Find(Exception error)
    { for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException!) if (error is SessionInterruption failure) return failure; return null; }
}
