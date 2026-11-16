using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

/// <summary>First run against the old implementation: these tests expose the reviewed defects.</summary>
public class ProxyRegressionTest
{
    [Fact]
    public void EncodedCredentialCharacters_AreDecodedExactlyOnce()
    {
        Assert.Equal(new[] { "alice", "p@ss:word%40" },
            ProxyUtil.ParseProxyCreds("http://alice:p%40ss%3Aword%2540@gateway.example:8000"));
    }

    [Fact]
    public void NonEmptyInvalidProxy_MustNotSilentlyUseSystemNetwork()
    {
        Assert.Throws<FormatException>(() => ProxyUtil.BrowserArgsFor("http://alice:dummy@", true));
    }

    [Fact]
    public void ForceProxyWithoutAddress_MustFailClosed()
    {
        Assert.Throws<FormatException>(() => ProxyUtil.BrowserArgsFor("", true));
    }

    [Theory]
    [InlineData("http://alice:pa@ss@gateway.example:8000")]
    [InlineData("http://alice:pa/ss@gateway.example:8000")]
    [InlineData("http://gateway.example:8000/path")]
    [InlineData("http://gateway.example:8000?query")]
    [InlineData("http://gateway.example:8000#fragment")]
    [InlineData("socks4://gateway.example:1080")]
    public void AmbiguousOrUnsupportedProxy_IsRejected(string address)
    {
        Assert.Throws<FormatException>(() => ProxyUtil.ProxyServerValue(address));
    }

    [Theory]
    [InlineData("http://alice:dummy@gateway.example:8000")]
    [InlineData("socks5://alice:dummy@gateway.example:1080")]
    public void BrowserArguments_DoNotClaimUnsupportedBasicAuthSwitch(string address)
    {
        Assert.DoesNotContain("--proxy-auth-scheme=basic", ProxyUtil.BrowserArgsFor(address));
    }
}
