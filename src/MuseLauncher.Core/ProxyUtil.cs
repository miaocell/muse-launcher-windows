using System.Net;
using System.Net.Sockets;

namespace MuseLauncher.Core;

/// <summary>代理地址工具（镜像安卓 ProxyHelper / AccountConfigActivity 的纯函数部分）。</summary>
public static class ProxyUtil
{
    /// <summary>
    /// Clash 常见端口：7890 混合(HTTP+SOCKS) / 7891 纯 HTTP / 7892 纯 SOCKS / 7897 Clash Verge Rev 默认混合端口
    /// </summary>
    public static readonly int[] ClashPorts = { 7890, 7897, 7891, 7892 };

    /// <summary>Clash Verge Rev 等常用客户端的纯 SOCKS 端口（其余按 HTTP 混合端口处理）。</summary>
    private static readonly HashSet<int> SocksOnlyPorts = new() { 7892 };

    /// <summary>统一代理地址格式：补全 scheme（默认 http）。</summary>
    public static string Normalize(string p)
    {
        var lower = p.ToLowerInvariant();
        if (lower.StartsWith("http://") || lower.StartsWith("https://")
            || lower.StartsWith("socks5://") || lower.StartsWith("socks4://"))
        {
            return p;
        }
        return "http://" + p;
    }

    /// <summary>
    /// Chromium 的 --proxy-server 不接受 URL 内嵌账密（会解析失败导致代理完全不生效），
    /// 这里剥离 user:pass 只保留 scheme://host:port，账密改由 407 挑战应答提供。
    /// </summary>
    public static string ProxyServerValue(string proxy)
    {
        var normalized = Normalize(proxy.Trim());
        var at = normalized.IndexOf('@');
        if (at < 0) return normalized;
        var schemeEnd = normalized.IndexOf("://", StringComparison.Ordinal);
        var rest = normalized[(at + 1)..];
        if (rest.Length == 0) return ""; // 只有账密没有主机：视为无效代理
        return schemeEnd < 0 ? rest : normalized[..(schemeEnd + 3)] + rest;
    }

    /// <summary>从代理地址解析 [user, pass]，无凭证返回 null。</summary>
    public static string[]? ParseProxyCreds(string p)
    {
        var at = p.IndexOf('@');
        if (at < 0) return null;
        var head = p.Substring(0, at);
        var si = head.IndexOf("://", StringComparison.Ordinal);
        var creds = si >= 0 ? head[(si + 3)..] : head;
        if (creds.Length == 0) return null;
        var c = creds.IndexOf(':');
        if (c < 0) return new[] { creds, "" };
        return new[] { creds[..c], creds[(c + 1)..] };
    }

    /// <summary>代理主机名（用于判断 407 挑战是否来自该代理），解析失败返回 null。</summary>
    public static string? ProxyHost(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;
        return Uri.TryCreate(Normalize(proxy.Trim()), UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    /// <summary>7892 是纯 SOCKS，其余按 HTTP 混合端口处理。</summary>
    public static string ProxyUrlForPort(int port)
    {
        return (SocksOnlyPorts.Contains(port) ? "socks5://" : "http://") + "127.0.0.1:" + port;
    }

    /// <summary>探测本机 Clash 默认端口，返回首个在监听的端口，未找到返回 0。</summary>
    public static int ProbeLocalProxy()
    {
        foreach (var p in ClashPorts)
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var ar = s.BeginConnect(new IPEndPoint(IPAddress.Loopback, p), null, null);
                if (ar.AsyncWaitHandle.WaitOne(400) && s.Connected) return p;
            }
            catch
            {
                // 端口不通，尝试下一个
            }
        }
        return 0;
    }

    /// <summary>
    /// WebView2 环境的代理启动参数。空代理返回空列表（跟随系统网络）。
    /// 只输出 scheme://host:port；账密由 CoreWebView2.BasicAuthenticationRequested 应答 407。
    /// </summary>
    public static List<string> BrowserArgsFor(string? proxy)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            args.Add("--proxy-server=" + ProxyServerValue(proxy));
        }
        return args;
    }
}

