using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class ProxyConfigurationTest
{
    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("gateway.example:8000", "http://gateway.example:8000")]
    [InlineData("HTTPS://Gateway.Example", "https://gateway.example:443")]
    [InlineData("socks5://[::1]", "socks5://[::1]:1080")]
    [InlineData("http://u:p@Gateway.Example.:80", "http://gateway.example:80")]
    [InlineData("http://例子.测试:80", "http://xn--fsqu00a.xn--0zwm56d:80")]
    public void Parse_CanonicalizesAndHidesCredentials(string input, string? expected)
    {
        var proxy = ProxyConfiguration.Parse(input);
        Assert.Equal(expected, proxy?.DisplayAddress);
        Assert.Equal(expected, proxy?.ToString());
        if (proxy is not null)
        {
            Assert.Equal("", proxy.ServerUri.UserInfo);
            Assert.DoesNotContain("@", proxy.DisplayAddress);
        }
    }

    [Fact]
    public void Credentials_UnicodePercentEncodingAndDecodeOnce()
    {
        var proxy = ProxyConfiguration.Parse("http://alice%2Btag:p%40ss%3Aword%2F%3F%23%25%2540@gateway.example:80")!;
        Assert.Equal("alice+tag", proxy.Username);
        Assert.Equal("p@ss:word/?#%%40", proxy.Password);
        var socks = ProxyConfiguration.Parse("socks5://%E7%94%A8%E6%88%B7:%E5%AF%86%E7%A0%81@127.0.0.1:1080")!;
        Assert.Equal("用户", socks.Username);
        Assert.Equal("密码", socks.Password);
    }

    [Fact]
    public void Equality_IncludesCaseSensitiveCredentialsAndCanonicalEndpoint()
    {
        var first = ProxyConfiguration.Parse("http://alice:pass@GATEWAY.EXAMPLE:80");
        Assert.Equal(first, ProxyConfiguration.Parse("gateway.example:80") is { } noAuth ?
            ProxyConfiguration.Parse("http://alice:pass@" + noAuth.Host + ":" + noAuth.Port) : null);
        Assert.NotEqual(first, ProxyConfiguration.Parse("http://Alice:pass@gateway.example:80"));
        Assert.NotEqual(first, ProxyConfiguration.Parse("http://alice:Pass@gateway.example:80"));
        Assert.NotEqual(first, ProxyConfiguration.Parse("http://gateway.example:80"));
        Assert.True(first == ProxyConfiguration.Parse("http://alice:pass@gateway.example:80"));
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("http://user:secret@")]
    [InlineData("http://@host:80")]
    [InlineData("http://:secret@host:80")]
    [InlineData("http://u:p@host:")]
    [InlineData("http://host:0")]
    [InlineData("http://host:65536")]
    [InlineData("http://host:-1")]
    [InlineData("http://host:+80")]
    [InlineData("http://host:80/")]
    [InlineData("http://host:80/path")]
    [InlineData("http://host:80?")]
    [InlineData("http://host:80#")]
    [InlineData("http://u:pa@ss@host:80")]
    [InlineData("http://u:p%0d%0a@host:80")]
    [InlineData("http://u:p%00@host:80")]
    [InlineData("http://u:p%GG@host:80")]
    [InlineData("http://u:p%@host:80")]
    [InlineData("http://u:p%FF@host:80")]
    [InlineData("http://u%3Ax:p@host:80")]
    [InlineData("socks4://host:1080")]
    [InlineData("socks://host:1080")]
    [InlineData("ftp://host:80")]
    [InlineData("http://host:80,direct://")]
    [InlineData("http://host:80 --no-proxy-server")]
    [InlineData("http://bad_host:80")]
    [InlineData("http://[::1%25zone]:80")]
    [InlineData("http://::1:80")]
    [InlineData("http://[::1]:")]
    [InlineData("http://host:80\n")]
    [InlineData("\thost:80")]
    [InlineData("socks5://user:@host:1080")]
    public void InvalidInput_IsRejectedWithoutEchoingSecrets(string input)
    {
        Assert.Throws<FormatException>(() => ProxyConfiguration.Parse(input));
        Assert.False(ProxyConfiguration.TryParse(input, out var proxy, out var error));
        Assert.Null(proxy);
        Assert.NotNull(error);
        Assert.DoesNotContain("secret", error);
    }

    [Fact]
    public void SocksCredentials_AreLimitedByUtf8BytesNotCharacters()
    {
        Assert.NotNull(ProxyConfiguration.Parse("socks5://" + new string('u', 255) + ":p@host:1080"));
        Assert.Throws<FormatException>(() => ProxyConfiguration.Parse("socks5://" + new string('u', 256) + ":p@host:1080"));
        Assert.Throws<FormatException>(() => ProxyConfiguration.Parse("socks5://u:" + new string('密', 86) + "@host:1080"));
    }

    [Fact]
    public void HttpsScheme_IsRetainedInServerUri()
    {
        var proxy = ProxyConfiguration.Parse("https://alice:dummy@localhost:443")!;
        Assert.Equal("https", proxy.ServerUri.Scheme);
        Assert.Equal(443, proxy.Port);
        Assert.Equal(ProxyProtocol.Https, proxy.Protocol);
    }
}
