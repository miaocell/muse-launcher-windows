using System.Net;
using System.Net.Sockets;
using System.Text;
using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class ProxyBridgeSocksTest
{
    [Theory]
    [InlineData(true, "target.invalid:443")]
    [InlineData(false, "target.invalid:443")]
    [InlineData(true, "[::1]:8443")]
    [InlineData(false, "127.0.0.1:8080")]
    public async Task Socks5_RealMethodAndUsernamePasswordHandshake_DomainResolvedUpstream(bool credentials, string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var destinationSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var stream = client.GetStream();
            destinationSeen.TrySetResult(await ProxyWireTest.SocksRequestAsync(stream, credentials, "用户", "p@ss:密码%40", token));
            // Domain-form bind reply plus payload in one write verifies handshake does not consume tunneled bytes.
            await stream.WriteAsync(new byte[] { 5, 0, 0, 3, 1, (byte)'x', 0, 0 }, token);
            await ProxyWireTest.SendAsync(stream, "socks-prefix", token);
            await ProxyWireTest.EchoAsync(stream, token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "socks5",
            credentials ? "用户" : null, "p@ss:密码%40"));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge, target), deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(target, await destinationSeen.Task.WaitAsync(deadline.Token));
        var source = new ProxyPrefixedStream(client.GetStream(), response.Remainder);
        var prefix = new byte[12];
        await source.ReadExactlyAsync(prefix, deadline.Token);
        Assert.Equal("socks-prefix", Encoding.ASCII.GetString(prefix));
        var payload = Enumerable.Range(0, 70000).Select(i => (byte)(i % 251)).ToArray();
        var received = new byte[payload.Length];
        var read = source.ReadExactlyAsync(received, deadline.Token).AsTask();
        await client.GetStream().WriteAsync(payload, deadline.Token);
        await read;
        Assert.Equal(payload, received);
        client.Client.Shutdown(SocketShutdown.Send);
        Assert.Empty(await ProxyWireTest.ReadToEndAsync(source, deadline.Token));
    }

    [Fact]
    public async Task Socks5_AuthenticationFailureIs502_NotLocal407()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            Assert.Equal("", await ProxyWireTest.SocksRequestAsync(client.GetStream(), true, "upstream-user", "upstream-password", token, false));
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "socks5"));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Values("Proxy-Authenticate"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public async Task Socks5_UnexpectedOrRejectedAuthMethodIsNotSilentlyDowngraded(int selected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var greeting = new byte[3];
            await client.GetStream().ReadExactlyAsync(greeting, token);
            Assert.Equal(new byte[] { 5, 1, 2 }, greeting);
            await client.GetStream().WriteAsync(new byte[] { 5, (byte)selected }, token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "socks5"));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        Assert.Equal(502, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
    }

    [Fact]
    public async Task Socks5_ConnectRejectionIs502()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            _ = await ProxyWireTest.SocksRequestAsync(client.GetStream(), false, "", "", token);
            await client.GetStream().WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "socks5", null));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        Assert.Equal(502, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
    }

    [Fact]
    public async Task Socks5_OrdinaryHttpUsesOriginForm_AndNeverSendsProxyCredentialsToTarget()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new TaskCompletionSource<ProxyHttpHead>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            Assert.Equal("target.invalid:8080", await ProxyWireTest.SocksRequestAsync(client.GetStream(), true, "upstream-user", "upstream-password", token));
            await client.GetStream().WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            seen.TrySetResult(head);
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nsafe", token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "socks5"));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "GET http://target.invalid:8080/path?q=1 HTTP/1.1\r\nProxy-Authorization: "
            + ProxyWireTest.LocalBasic(bridge) + "\r\nAuthorization: Bearer target-token\r\nProxy-Connection: keep-alive\r\n\r\n", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("safe", Encoding.ASCII.GetString(await ProxyWireTest.ReadToEndAsync(new ProxyPrefixedStream(client.GetStream(), response.Remainder), deadline.Token)));
        var targetRequest = await seen.Task.WaitAsync(deadline.Token);
        Assert.Equal("GET /path?q=1 HTTP/1.1", targetRequest.StartLine);
        Assert.Empty(targetRequest.Values("Proxy-Authorization"));
        Assert.Empty(targetRequest.Values("Proxy-Connection"));
        Assert.Equal("Bearer target-token", Assert.Single(targetRequest.Values("Authorization")));
    }
}
