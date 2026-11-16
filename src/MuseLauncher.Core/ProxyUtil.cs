using System.Net;
using System.Net.Sockets;

namespace MuseLauncher.Core;

/// <summary>Validated proxy addresses and browser endpoint arguments. Authentication belongs to the local bridge.</summary>
public static class ProxyUtil
{
    public static readonly int[] ClashPorts = { 7890, 7897, 7891, 7892 };
    private static readonly HashSet<int> SocksOnlyPorts = new() { 7892 };

    /// <summary>Canonical credential-free address; invalid non-empty addresses are never treated as direct.</summary>
    public static string Normalize(string p) => ProxyConfiguration.Parse(p)?.DisplayAddress ?? "";
    public static string ProxyServerValue(string proxy) => Normalize(proxy);

    public static string[]? ParseProxyCreds(string p)
    {
        var configuration = ProxyConfiguration.Parse(p);
        return configuration?.HasCredentials == true ? [configuration.Username!, configuration.Password!] : null;
    }

    public static string? ProxyHost(string? proxy) => ProxyConfiguration.Parse(proxy)?.Host;
    public static bool HasCredentials(string? proxy) => ProxyConfiguration.Parse(proxy)?.HasCredentials == true;

    public static string ProxyUrlForPort(int port)
        => (SocksOnlyPorts.Contains(port) ? "socks5://" : "http://") + "127.0.0.1:" + port;

    /// <summary>Detects listening local ports, not whether an endpoint implements the proxy protocol.</summary>
    public static int ProbeLocalProxy()
    {
        foreach (var port in ClashPorts)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token).AsTask().GetAwaiter().GetResult();
                if (socket.Connected) return port;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        return 0;
    }

    public static List<string> BrowserArgsFor(string? proxy) => BrowserArgsFor(proxy, false);

    /// <summary>
    /// Generates endpoint arguments, never credentials or fictitious authentication switches.
    /// Production callers pass LoopbackProxyBridge.Address, not an authenticated upstream address.
    /// ForceProxy removes implicit HTTP bypass rules; it is not a process-wide network kill switch.
    /// </summary>
    public static List<string> BrowserArgsFor(string? proxy, bool forceProxy)
    {
        var configuration = ProxyConfiguration.Parse(proxy);
        if (configuration is null)
        {
            if (forceProxy) throw new FormatException("A valid proxy address is required when forcing proxy use.");
            return [];
        }
        var args = new List<string> { "--proxy-server=" + configuration.DisplayAddress };
        if (forceProxy) args.Add("--proxy-bypass-list=<-loopback>");
        return args;
    }
}
