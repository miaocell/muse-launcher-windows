using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

/// <summary>
/// 代理参数生成。回归重点：Chromium 的 --proxy-server 不接受 URL 内嵌账密，
/// 若原样透传会让代理完全不生效（v1.0.1 用户反馈"配了代理没用"）。
/// </summary>
public class ProxyUtilTest
{
    [Fact]
    public void Normalize_AddsHttpSchemeWhenMissing()
    {
        Assert.Equal("http://127.0.0.1:7890", ProxyUtil.Normalize("127.0.0.1:7890"));
        Assert.Equal("socks5://127.0.0.1:1080", ProxyUtil.Normalize("socks5://127.0.0.1:1080"));
        Assert.Equal("http://user:p@1.2.3.4:3129", ProxyUtil.Normalize("user:p@1.2.3.4:3129"));
    }

    [Fact]
    public void BrowserArgs_PlainProxy_PassedAsIs()
    {
        var args = ProxyUtil.BrowserArgsFor("127.0.0.1:7890");
        Assert.Single(args);
        Assert.Equal("--proxy-server=http://127.0.0.1:7890", args[0]);
    }

    [Fact]
    public void BrowserArgs_EmptyProxy_NoArgs()
    {
        Assert.Empty(ProxyUtil.BrowserArgsFor(""));
        Assert.Empty(ProxyUtil.BrowserArgsFor("   "));
        Assert.Empty(ProxyUtil.BrowserArgsFor(null));
    }

    /// <summary>回归：内嵌账密必须剥离，否则 Chromium 无法解析、代理静默失效。</summary>
    [Fact]
    public void BrowserArgs_EmbeddedCredentials_Stripped()
    {
        var args = ProxyUtil.BrowserArgsFor("http://user:pass@1.2.3.4:3129");
        Assert.Single(args);
        Assert.Equal("--proxy-server=http://1.2.3.4:3129", args[0]);
        Assert.DoesNotContain("user", args[0]);
        Assert.DoesNotContain("pass", args[0]);
    }

    [Fact]
    public void BrowserArgs_SocksWithCreds_StrippedKeepsScheme()
    {
        var args = ProxyUtil.BrowserArgsFor("socks5://u:p@127.0.0.1:1080");
        Assert.Equal("--proxy-server=socks5://127.0.0.1:1080", args[0]);
    }

    [Fact]
    public void ProxyServerValue_HandlesEdgeCases()
    {
        Assert.Equal("http://1.2.3.4:3129", ProxyUtil.ProxyServerValue("http://u:p@1.2.3.4:3129"));
        Assert.Equal("http://1.2.3.4:3129", ProxyUtil.ProxyServerValue("u:p@1.2.3.4:3129"));
        Assert.Equal("http://127.0.0.1:7890", ProxyUtil.ProxyServerValue("127.0.0.1:7890"));
        // 只有账密没有主机（异常输入）不应抛异常
        Assert.Equal("", ProxyUtil.ProxyServerValue("u:p@"));
    }

    [Fact]
    public void ParseProxyCreds_ExtractsUserPass()
    {
        Assert.Equal(new[] { "user", "pass" }, ProxyUtil.ParseProxyCreds("http://user:pass@1.2.3.4:3129"));
        Assert.Equal(new[] { "user", "" }, ProxyUtil.ParseProxyCreds("http://user@1.2.3.4:3129"));
        Assert.Null(ProxyUtil.ParseProxyCreds("http://1.2.3.4:3129"));
        Assert.Null(ProxyUtil.ParseProxyCreds("http://@1.2.3.4:3129"));
    }

    [Fact]
    public void ProxyHost_ReturnsProxyHost()
    {
        Assert.Equal("1.2.3.4", ProxyUtil.ProxyHost("http://u:p@1.2.3.4:3129"));
        Assert.Equal("127.0.0.1", ProxyUtil.ProxyHost("127.0.0.1:7890"));
        Assert.Null(ProxyUtil.ProxyHost(""));
        Assert.Null(ProxyUtil.ProxyHost(null));
    }

    [Fact]
    public void ProxyUrlForPort_SocksOnlyFor7892()
    {
        Assert.Equal("http://127.0.0.1:7890", ProxyUtil.ProxyUrlForPort(7890));
        Assert.Equal("http://127.0.0.1:7897", ProxyUtil.ProxyUrlForPort(7897));
        Assert.Equal("http://127.0.0.1:7891", ProxyUtil.ProxyUrlForPort(7891));
        Assert.Equal("socks5://127.0.0.1:7892", ProxyUtil.ProxyUrlForPort(7892));
    }

    /// <summary>Clash Verge Rev 默认混合端口 7897 必须在探测列表里，否则"探测"永远找不到。</summary>
    [Fact]
    public void ClashPorts_IncludesVergeDefault()
    {
        Assert.Contains(7897, ProxyUtil.ClashPorts);
    }
}
