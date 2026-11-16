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
        Assert.Equal("http://1.2.3.4:3129", ProxyUtil.Normalize("user:p@1.2.3.4:3129"));
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
        Assert.Contains("--proxy-server=http://1.2.3.4:3129", args);
        Assert.DoesNotContain(args, a => a.Contains("user") || a.Contains("pass"));
    }

    [Fact]
    public void BrowserArgs_SocksWithCreds_StrippedKeepsScheme()
    {
        var args = ProxyUtil.BrowserArgsFor("socks5://u:p@127.0.0.1:1080");
        Assert.Contains("--proxy-server=socks5://127.0.0.1:1080", args);
        Assert.DoesNotContain(args, a => a.Contains("u:") || a.Contains(":p@"));
    }

    /// <summary>A missing host is invalid; it must not silently choose the system network.</summary>
    [Fact]
    public void ProxyServerValue_HandlesEdgeCases()
    {
        Assert.Equal("http://1.2.3.4:3129", ProxyUtil.ProxyServerValue("http://u:p@1.2.3.4:3129"));
        Assert.Equal("http://1.2.3.4:3129", ProxyUtil.ProxyServerValue("u:p@1.2.3.4:3129"));
        Assert.Equal("http://127.0.0.1:7890", ProxyUtil.ProxyServerValue("127.0.0.1:7890"));
        Assert.Throws<FormatException>(() => ProxyUtil.ProxyServerValue("u:p@"));
    }

    [Fact]
    public void ParseProxyCreds_ExtractsUserPass()
    {
        Assert.Equal(new[] { "user", "pass" }, ProxyUtil.ParseProxyCreds("http://user:pass@1.2.3.4:3129"));
        Assert.Equal(new[] { "user", "" }, ProxyUtil.ParseProxyCreds("http://user@1.2.3.4:3129"));
        Assert.Null(ProxyUtil.ParseProxyCreds("http://1.2.3.4:3129"));
        Assert.Throws<FormatException>(() => ProxyUtil.ParseProxyCreds("http://@1.2.3.4:3129"));
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

    // ---------- 1024proxy 一类带账密的住宅代理 ----------

    /// <summary>Authentication is implemented by the local bridge, not a fictitious Chromium switch.</summary>
    [Fact]
    public void BrowserArgs_WithCredentials_NoFictitiousAuthScheme()
    {
        var args = ProxyUtil.BrowserArgsFor("http://user:pass@1.2.3.4:3129");
        Assert.Contains("--proxy-server=http://1.2.3.4:3129", args);
        Assert.DoesNotContain("--proxy-auth-scheme=basic", args);
    }

    [Fact]
    public void BrowserArgs_WithoutCredentials_NoAuthScheme()
    {
        var args = ProxyUtil.BrowserArgsFor("127.0.0.1:7897");
        Assert.Contains("--proxy-server=http://127.0.0.1:7897", args);
        Assert.DoesNotContain("--proxy-auth-scheme=basic", args);
    }

    [Fact]
    public void BrowserArgs_Socks5WithCredentials_NoFictitiousAuthScheme()
    {
        var args = ProxyUtil.BrowserArgsFor("socks5://user:pass@gate.1024proxy.io:1080");
        Assert.Contains("--proxy-server=socks5://gate.1024proxy.io:1080", args);
        Assert.DoesNotContain("--proxy-auth-scheme=basic", args);
    }

    /// <summary>强制走代理时用 &lt;-loopback&gt; 取消本机直连豁免，避免真实 IP 泄露。</summary>
    [Fact]
    public void BrowserArgs_ForceProxy_AddsBypassOverride()
    {
        var args = ProxyUtil.BrowserArgsFor("http://user:pass@1.2.3.4:3129", forceProxy: true);
        Assert.Contains("--proxy-bypass-list=<-loopback>", args);
    }

    [Fact]
    public void BrowserArgs_NotForced_NoBypassOverride()
    {
        var args = ProxyUtil.BrowserArgsFor("http://1.2.3.4:3129");
        Assert.DoesNotContain("--proxy-bypass-list=<-loopback>", args);
    }

    /// <summary>ForceProxy without an endpoint must fail closed.</summary>
    [Fact]
    public void BrowserArgs_EmptyProxy_ForceFailsClosed()
    {
        Assert.Throws<FormatException>(() => ProxyUtil.BrowserArgsFor("", forceProxy: true));
        Assert.Throws<FormatException>(() => ProxyUtil.BrowserArgsFor(null, forceProxy: true));
    }

    [Fact]
    public void BrowserArgs_CredentialsOnly_Invalid()
    {
        Assert.Throws<FormatException>(() => ProxyUtil.BrowserArgsFor("http://user:pass@"));
    }

    [Theory]
    [InlineData("http://u:p@1.2.3.4:3129", true)]
    [InlineData("socks5://u:p@h:1080", true)]
    [InlineData("127.0.0.1:7897", false)]
    [InlineData("", false)]
    public void HasCredentials_Works(string proxy, bool expected)
    {
        Assert.Equal(expected, ProxyUtil.HasCredentials(proxy));
    }

    /// <summary>每个账号的代理互不影响：同一方法对不同账号输入产出不同参数（多账号分代理的核心保证）。</summary>
    [Fact]
    public void BrowserArgs_PerAccountIsolation()
    {
        var a = ProxyUtil.BrowserArgsFor("http://u1:p1@gate-a.example:8000");
        var b = ProxyUtil.BrowserArgsFor("http://u2:p2@gate-b.example:8000");

        Assert.Contains("--proxy-server=http://gate-a.example:8000", a);
        Assert.Contains("--proxy-server=http://gate-b.example:8000", b);
        Assert.DoesNotContain(a, x => x.Contains("gate-b"));
        Assert.DoesNotContain(b, x => x.Contains("gate-a"));
        // 账密不得出现在任何启动参数中（泄漏到进程命令行）
        Assert.DoesNotContain(a, x => x.Contains("u1") || x.Contains("p1"));
        Assert.DoesNotContain(b, x => x.Contains("u2") || x.Contains("p2"));
    }
}
