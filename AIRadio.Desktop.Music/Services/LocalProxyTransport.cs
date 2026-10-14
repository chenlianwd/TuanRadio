using System;
using System.Net.Http;
using System.Net.Sockets;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 本地代理传输层失败判定：连接拒绝/重置 = 代理进程未监听（死亡或未启动）。
/// HTTP 非 2xx（服务器活着、业务失败——酷狗代理即用 502 + JSON 表达）不算传输层失败。
/// 自愈 handler（触发重启）与扫码轮询状态映射（ServiceUnavailable）共用，防止两处判定漂移。
/// </summary>
public static class LocalProxyTransport
{
    public static bool IsConnectFailure(HttpRequestException exception)
    {
        for (var e = (Exception?)exception; e != null; e = e.InnerException)
        {
            if (e is SocketException
                {
                    SocketErrorCode: SocketError.ConnectionRefused or SocketError.ConnectionReset
                })
                return true;
        }

        return false;
    }
}
