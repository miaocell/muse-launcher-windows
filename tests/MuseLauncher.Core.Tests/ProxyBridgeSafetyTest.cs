using System.Net;
using System.Net.Sockets;
using System.Text;
using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class ProxyBridgeSafetyTest
{
    [Theory]
    [InlineData("https://target.invalid/", "GET")]
    [InlineData("http://u:p@target.invalid/", "GET")]
    [InlineData("http://target.invalid/#fragment", "GET")]
    [InlineData("target.invalid", "CONNECT")]
    [InlineData("target.invalid:0", "CONNECT")]
    [InlineData("target.invalid:65536", "CONNECT")]
    [InlineData("::1:443", "CONNECT")]
    [InlineData("[::1]:443/path", "CONNECT")]
    public async Task InvalidDestination_IsRejectedBeforeAnyUpstreamConnect(string target, string method)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), method + " " + target + " HTTP/1.1\r\nProxy-Authorization: "
            + ProxyWireTest.LocalBasic(bridge) + "\r\n\r\n", deadline.Token);
        Assert.Equal(400, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
        Assert.Equal(0, fixture.Accepted);
    }

    [Theory]
    [InlineData("Proxy-Authorization: Basic invalid\r\n")]
    [InlineData("Proxy-Authorization: Basic YWxpY2U6ZHVtbXk=\r\n")]
    [InlineData("Proxy-Authorization: Bearer invalid\r\n")]
    public async Task InvalidLocalAuth_ReturnsOnlyLocal407(string auth)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "CONNECT target.invalid:443 HTTP/1.1\r\n" + auth + "\r\n", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(407, response.StatusCode);
        Assert.Equal("Basic realm=\"" + LoopbackProxyBridge.AuthenticationRealm + "\"", Assert.Single(response.Values("Proxy-Authenticate")));
        Assert.Equal(0, fixture.Accepted);
    }

    [Fact]
    public async Task DuplicateAuthOrAmbiguousBody_IsRejected()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using (var duplicate = await ProxyWireTest.ConnectAsync(bridge, deadline.Token))
        {
            await ProxyWireTest.SendAsync(duplicate.GetStream(), "CONNECT target.invalid:443 HTTP/1.1\r\nProxy-Authorization: "
                + ProxyWireTest.LocalBasic(bridge) + "\r\nProxy-Authorization: " + ProxyWireTest.LocalBasic(bridge) + "\r\n\r\n", deadline.Token);
            Assert.Equal(407, (await ProxyWireTest.HeadAsync(duplicate.GetStream(), deadline.Token)).StatusCode);
        }
        using var body = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(body.GetStream(), "POST http://target.invalid/ HTTP/1.1\r\nProxy-Authorization: "
            + ProxyWireTest.LocalBasic(bridge) + "\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n", deadline.Token);
        Assert.Equal(400, (await ProxyWireTest.HeadAsync(body.GetStream(), deadline.Token)).StatusCode);
        Assert.Equal(0, fixture.Accepted);
    }

    [Fact]
    public async Task HeaderLimit_AndSlowHeaders_AreBounded()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture),
            new ProxyBridgeOptions { HeaderLimit = 1024, HeaderTimeout = TimeSpan.FromMilliseconds(200) });
        using (var large = await ProxyWireTest.ConnectAsync(bridge, deadline.Token))
        {
            await ProxyWireTest.SendAsync(large.GetStream(), "CONNECT target.invalid:443 HTTP/1.1\r\nX-Large: " + new string('x', 1100), deadline.Token);
            Assert.Equal(431, (await ProxyWireTest.HeadAsync(large.GetStream(), deadline.Token)).StatusCode);
        }
        using var slow = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(slow.GetStream(), "CONNECT ", deadline.Token);
        Assert.Equal(504, (await ProxyWireTest.HeadAsync(slow.GetStream(), deadline.Token)).StatusCode);
        Assert.Equal(0, fixture.Accepted);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task UpstreamHandshakeTimeout_Returns504AndClosesSockets(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, protocol),
            new ProxyBridgeOptions { ConnectTimeout = TimeSpan.FromMilliseconds(200) });
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        Assert.Equal(504, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
        await ProxyWireTest.WaitForAsync(() => bridge.ActiveConnections == 0, deadline.Token);
        Assert.Equal(1, fixture.Accepted);
    }

    [Fact]
    public async Task UnreachableUpstream_DoesNotConnectDirectlyToReachableTarget()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var target = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var unavailable = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyConfiguration.Parse("http://127.0.0.1:" + unavailable)!);
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge, "127.0.0.1:" + target.Port), deadline.Token);
        Assert.Equal(502, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
        Assert.Equal(0, target.Accepted);
    }

    [Fact]
    public async Task EveryOrdinaryRequestRequiresLocalAuth_NoKeepAliveAuthReuse()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            _ = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n", token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "GET http://first.invalid/ HTTP/1.1\r\nProxy-Authorization: " + ProxyWireTest.LocalBasic(bridge)
            + "\r\n\r\nGET http://second.invalid/ HTTP/1.1\r\n\r\n", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(204, response.StatusCode);
        Assert.Empty(await ProxyWireTest.ReadToEndAsync(new ProxyPrefixedStream(client.GetStream(), response.Remainder), deadline.Token));
        Assert.Equal(1, fixture.Accepted);
    }

    [Fact]
    public async Task Dispose_ClosesActiveTunnelListenerAndAllTasks_Idempotently()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var upstreamClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            _ = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 Connected\r\n\r\n", token);
            Assert.Empty(await ProxyWireTest.ReadToEndAsync(client.GetStream(), token));
            upstreamClosed.TrySetResult();
        });
        var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        Assert.Equal(200, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
        Assert.Equal(1, bridge.ActiveConnections);
        await bridge.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        await bridge.DisposeAsync();
        await bridge.Completion.WaitAsync(deadline.Token);
        Assert.Equal(0, bridge.ActiveConnections);
        Assert.Empty(await ProxyWireTest.ReadToEndAsync(client.GetStream(), deadline.Token));
        await upstreamClosed.Task.WaitAsync(deadline.Token);
        using var closed = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => closed.ConnectAsync(IPAddress.Loopback, bridge.Address.Port, deadline.Token).AsTask());
    }

    [Fact]
    public async Task Dispose_CancelsBlockedClientHeadersAndUpstreamHandshake()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var headerClient = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(headerClient.GetStream(), "CONNECT ", deadline.Token);
        using var handshakeClient = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(handshakeClient.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        await ProxyWireTest.WaitForAsync(() => fixture.Accepted == 1 && bridge.ActiveConnections == 2, deadline.Token);
        await bridge.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        Assert.True(bridge.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, bridge.ActiveConnections);
    }

    [Fact]
    public async Task ConnectionCapacity_IsBoundedBeforeSpawningMoreTasks()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture), new ProxyBridgeOptions { MaxConnections = 2 });
        using var first = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        using var second = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.WaitForAsync(() => bridge.ActiveConnections == 2, deadline.Token);
        using var third = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        Assert.Empty(await ProxyWireTest.ReadToEndAsync(third.GetStream(), deadline.Token));
        Assert.Equal(2, bridge.ActiveConnections);
        await bridge.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        Assert.Equal(0, fixture.Accepted);
    }
}
