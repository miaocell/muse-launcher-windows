using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class ProxyBridgeHttpTest
{
    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(15));

    [Fact]
    public async Task Startup_DoesNotConnectUpstream_AndLocal407IsNotUpstreamRealm()
    {
        using var deadline = TestDeadline();
        await using var fixture = new ProxyLoopbackFixture((_, token) => Task.Delay(Timeout.Infinite, token));
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        Assert.Equal(0, fixture.Accepted);
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "CONNECT target.invalid:443 HTTP/1.1\r\n\r\n", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(407, response.StatusCode);
        Assert.Equal("Basic realm=\"MuseLauncher Local Proxy\"", Assert.Single(response.Values("Proxy-Authenticate")));
        Assert.Equal(0, fixture.Accepted);
        Assert.NotEqual("upstream-user", bridge.Username);
        Assert.NotEqual("upstream-password", bridge.Password);
    }

    [Fact]
    public async Task StartupToken_CancelAfterSuccess_DoesNotStopPublishedBridge()
    {
        using var deadline = TestDeadline();
        using var startup = new CancellationTokenSource();
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            Assert.StartsWith("CONNECT ", head.StartLine);
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 Connected\r\n\r\n", token);
            await ProxyWireTest.EchoAsync(client.GetStream(), token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture), startup.Token);
        startup.Cancel();
        Assert.False(bridge.Completion.IsCompleted);
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        Assert.Equal(200, (await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token)).StatusCode);
    }

    [Fact]
    public async Task Startup_PreCancelled_RejectsBeforeStarting()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoopbackProxyBridge.StartAsync(
            ProxyConfiguration.Parse("http://127.0.0.1:1")!, cancelled.Token));
    }

    [Fact]
    public async Task Connect_BasicPreauthAndBidirectionalPayload_KeepSecretsOnlyAtProxy()
    {
        using var deadline = TestDeadline();
        var seen = new TaskCompletionSource<ProxyHttpHead>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var stream = client.GetStream();
            var head = await ProxyWireTest.HeadAsync(stream, token);
            seen.TrySetResult(head);
            await ProxyWireTest.SendAsync(stream, "HTTP/1.1 200 Connected\r\nProxy-Authenticate: Basic realm=\"upstream-secret-realm\"\r\n\r\nserver-prefix", token);
            await ProxyWireTest.EchoAsync(new ProxyPrefixedStream(stream, head.Remainder), token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, user: "alice+session", password: "p@ss:word%40"));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        var stream = client.GetStream();
        await ProxyWireTest.SendAsync(stream, ProxyWireTest.ConnectRequest(bridge) + "initial-payload", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(stream, deadline.Token);
        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response.Values("Proxy-Authenticate"));
        var source = new ProxyPrefixedStream(stream, response.Remainder);
        var expectedPrefix = Encoding.ASCII.GetBytes("server-prefixinitial-payload");
        var prefix = new byte[expectedPrefix.Length];
        await source.ReadExactlyAsync(prefix, deadline.Token);
        Assert.Equal(expectedPrefix, prefix);
        var payload = Enumerable.Range(0, 150000).Select(i => (byte)(i % 251)).ToArray();
        var received = new byte[payload.Length];
        var reading = source.ReadExactlyAsync(received, deadline.Token).AsTask();
        await stream.WriteAsync(payload, deadline.Token);
        await reading;
        Assert.Equal(payload, received);
        var upstream = await seen.Task.WaitAsync(deadline.Token);
        Assert.Equal("CONNECT target.invalid:443 HTTP/1.1", upstream.StartLine);
        Assert.Equal(ProxyWireTest.Basic("alice+session", "p@ss:word%40"), Assert.Single(upstream.Values("Proxy-Authorization")));
        Assert.DoesNotContain(bridge.Username, Assert.Single(upstream.Values("Proxy-Authorization")));
        Assert.Equal(1, fixture.Accepted);
        client.Client.Shutdown(SocketShutdown.Send);
        Assert.Empty(await ProxyWireTest.ReadToEndAsync(source, deadline.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upstream407_Is502_NotForwardedToBrowser(bool connect)
    {
        using var deadline = TestDeadline();
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            _ = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"upstream-user:upstream-password\"\r\nContent-Length: 14\r\n\r\nupstream-secret", token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), connect ? ProxyWireTest.ConnectRequest(bridge)
            : "GET http://target.invalid/path HTTP/1.1\r\nProxy-Authorization: " + ProxyWireTest.LocalBasic(bridge) + "\r\n\r\n", deadline.Token);
        var response = Encoding.ASCII.GetString(await ProxyWireTest.ReadToEndAsync(client.GetStream(), deadline.Token));
        Assert.StartsWith("HTTP/1.1 502 ", response);
        Assert.DoesNotContain("Proxy-Authenticate", response);
        Assert.DoesNotContain("upstream-secret", response);
        Assert.DoesNotContain("upstream-password", response);
    }

    [Fact]
    public async Task OrdinaryHttp_AbsoluteForm_StripsLocalAndHopHeaders_PreservesEndToEndAuthorization()
    {
        using var deadline = TestDeadline();
        var seen = new TaskCompletionSource<ProxyHttpHead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodySeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            seen.TrySetResult(head);
            var body = new byte[5];
            await new ProxyPrefixedStream(client.GetStream(), head.Remainder).ReadExactlyAsync(body, token);
            bodySeen.TrySetResult(Encoding.ASCII.GetString(body));
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nProxy-Authenticate: Basic realm=\"secret\"\r\nConnection: X-Hop\r\nX-Hop: remove\r\n\r\nreply", token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "POST http://target.invalid:8080/path?q=1 HTTP/1.1\r\nHost: incorrect\r\n"
            + "Proxy-Authorization: " + ProxyWireTest.LocalBasic(bridge) + "\r\nProxy-Connection: keep-alive\r\n"
            + "Connection: keep-alive, X-Hop\r\nX-Hop: local-sensitive\r\nAuthorization: Bearer target-token\r\nContent-Length: 5\r\n\r\nhello", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response.Values("Proxy-Authenticate"));
        Assert.Empty(response.Values("X-Hop"));
        Assert.Equal("reply", Encoding.ASCII.GetString(await ProxyWireTest.ReadToEndAsync(new ProxyPrefixedStream(client.GetStream(), response.Remainder), deadline.Token)));
        var outgoing = await seen.Task.WaitAsync(deadline.Token);
        Assert.Equal("POST http://target.invalid:8080/path?q=1 HTTP/1.1", outgoing.StartLine);
        Assert.Equal("target.invalid:8080", Assert.Single(outgoing.Values("Host")));
        Assert.Empty(outgoing.Values("Proxy-Connection"));
        Assert.Empty(outgoing.Values("X-Hop"));
        Assert.Equal("close", Assert.Single(outgoing.Values("Connection")));
        Assert.Equal("Bearer target-token", Assert.Single(outgoing.Values("Authorization")));
        Assert.Equal(ProxyWireTest.Basic("upstream-user", "upstream-password"), Assert.Single(outgoing.Values("Proxy-Authorization")));
        Assert.Equal("hello", await bodySeen.Task.WaitAsync(deadline.Token));
    }

    [Fact]
    public async Task OrdinaryHttp_ChunkedUploadAndDownload_StreamWithoutSensitiveTrailers()
    {
        using var deadline = TestDeadline();
        var uploaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            using var memory = new MemoryStream();
            await ProxyHttpProtocol.CopyBodyAsync(new ProxyPrefixedStream(client.GetStream(), head.Remainder), memory,
                ProxyBodyFraming.Parse(head), false, 16384, token);
            uploaded.TrySetResult(Encoding.ASCII.GetString(memory.ToArray()));
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTrailer: Proxy-Authenticate\r\n\r\n5\r\nreply\r\n0\r\nProxy-Authenticate: secret\r\n\r\n", token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "POST http://target.invalid/ HTTP/1.1\r\nProxy-Authorization: " + ProxyWireTest.LocalBasic(bridge)
            + "\r\nTransfer-Encoding: chunked\r\nTrailer: Proxy-Authorization\r\n\r\n5\r\nhello\r\n0\r\nProxy-Authorization: local-secret\r\n\r\n", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(200, response.StatusCode);
        var body = Encoding.ASCII.GetString(await ProxyWireTest.ReadToEndAsync(new ProxyPrefixedStream(client.GetStream(), response.Remainder), deadline.Token));
        Assert.Equal("5\r\nreply\r\n0\r\n\r\n", body);
        Assert.Equal("5\r\nhello\r\n0\r\n\r\n", await uploaded.Task.WaitAsync(deadline.Token));
    }

    [Fact]
    public async Task HttpClient_UsesRealLocal407Challenge_ThenAuthenticatedHttp()
    {
        using var deadline = TestDeadline();
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            Assert.Equal("GET http://target.invalid/probe HTTP/1.1", head.StartLine);
            Assert.Equal(ProxyWireTest.Basic("upstream-user", "upstream-password"), Assert.Single(head.Values("Proxy-Authorization")));
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n", token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy(bridge.Address)
            { Credentials = new NetworkCredential(bridge.Username, bridge.Password) }, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync("http://target.invalid/probe", deadline.Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, fixture.Accepted);
    }

    [Fact]
    public async Task WebSocketUpgrade_BecomesBidirectionalTunnel()
    {
        using var deadline = TestDeadline();
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            Assert.Equal("websocket", Assert.Single(head.Values("Upgrade")));
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n", token);
            await ProxyWireTest.EchoAsync(new ProxyPrefixedStream(client.GetStream(), head.Remainder), token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), "GET http://target.invalid/socket HTTP/1.1\r\nProxy-Authorization: "
            + ProxyWireTest.LocalBasic(bridge) + "\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n", deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(101, response.StatusCode);
        var source = new ProxyPrefixedStream(client.GetStream(), response.Remainder);
        await ProxyWireTest.SendAsync(client.GetStream(), "websocket-frame", deadline.Token);
        var result = new byte[15];
        await source.ReadExactlyAsync(result, deadline.Token);
        Assert.Equal("websocket-frame", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public async Task TwoBridges_IsolateUpstreamCredentialsAndTargets_RejectCrossLocalAuth()
    {
        using var deadline = TestDeadline();
        var firstTargets = new ConcurrentQueue<string>();
        var secondTargets = new ConcurrentQueue<string>();
        await using var first = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            firstTargets.Enqueue(head.StartLine);
            Assert.Equal(ProxyWireTest.Basic("account-one", "secret-one"), Assert.Single(head.Values("Proxy-Authorization")));
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\none", token);
        });
        await using var second = new ProxyLoopbackFixture(async (client, token) =>
        {
            var head = await ProxyWireTest.HeadAsync(client.GetStream(), token);
            secondTargets.Enqueue(head.StartLine);
            Assert.Equal(ProxyWireTest.Basic("account-two", "secret-two"), Assert.Single(head.Values("Proxy-Authorization")));
            await ProxyWireTest.SendAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\ntwo", token);
        });
        await using var bridge1 = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(first, user: "account-one", password: "secret-one"));
        await using var bridge2 = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(second, user: "account-two", password: "secret-two"));
        Assert.NotEqual(bridge1.Address, bridge2.Address);
        Assert.NotEqual(bridge1.Username, bridge2.Username);
        foreach (var (bridge, host, body) in new[] { (bridge1, "one.invalid", "one"), (bridge2, "two.invalid", "two") })
        {
            using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
            await ProxyWireTest.SendAsync(client.GetStream(), "GET http://" + host + "/ HTTP/1.1\r\nProxy-Authorization: " + ProxyWireTest.LocalBasic(bridge) + "\r\n\r\n", deadline.Token);
            var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
            Assert.Equal(body, Encoding.ASCII.GetString(await ProxyWireTest.ReadToEndAsync(new ProxyPrefixedStream(client.GetStream(), response.Remainder), deadline.Token)));
        }
        using var wrong = await ProxyWireTest.ConnectAsync(bridge2, deadline.Token);
        await ProxyWireTest.SendAsync(wrong.GetStream(), ProxyWireTest.ConnectRequest(bridge2, authorization: ProxyWireTest.LocalBasic(bridge1)), deadline.Token);
        Assert.Equal(407, (await ProxyWireTest.HeadAsync(wrong.GetStream(), deadline.Token)).StatusCode);
        Assert.Equal("GET http://one.invalid/ HTTP/1.1", Assert.Single(firstTargets));
        Assert.Equal("GET http://two.invalid/ HTTP/1.1", Assert.Single(secondTargets));
    }

    [Fact]
    public async Task HttpsUpstream_UsesTlsAndSpecificTrustedRoot_NotCleartextConnect()
    {
        using var deadline = TestDeadline();
        using var certificates = new ProxyTestCertificates();
        var seen = new TaskCompletionSource<ProxyHttpHead>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            await using var tls = await ProxyWireTest.ServerTlsAsync(client, certificates.Server, token);
            var head = await ProxyWireTest.HeadAsync(tls, token);
            seen.TrySetResult(head);
            await ProxyWireTest.SendAsync(tls, "HTTP/1.1 200 Connected\r\n\r\n", token);
            await ProxyWireTest.EchoAsync(new ProxyPrefixedStream(tls, head.Remainder), token);
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "https"),
            new ProxyBridgeOptions { TrustedRoots = new() { certificates.Root } });
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(ProxyWireTest.Basic("upstream-user", "upstream-password"), Assert.Single((await seen.Task.WaitAsync(deadline.Token)).Values("Proxy-Authorization")));
        await ProxyWireTest.SendAsync(client.GetStream(), "secure-proxy-payload", deadline.Token);
        var data = new byte[20];
        await new ProxyPrefixedStream(client.GetStream(), response.Remainder).ReadExactlyAsync(data, deadline.Token);
        Assert.Equal("secure-proxy-payload", Encoding.ASCII.GetString(data));
    }

    [Fact]
    public async Task HttpsUpstream_UntrustedCertificateIsRejected_WithoutSendingCredentials()
    {
        using var deadline = TestDeadline();
        using var certificates = new ProxyTestCertificates();
        var receivedHttp = false;
        await using var fixture = new ProxyLoopbackFixture(async (client, token) =>
        {
            try
            {
                await using var tls = await ProxyWireTest.ServerTlsAsync(client, certificates.Server, token);
                var one = new byte[1];
                receivedHttp = await tls.ReadAsync(one, token) > 0;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        });
        await using var bridge = await LoopbackProxyBridge.StartAsync(ProxyWireTest.Configuration(fixture, "https"));
        using var client = await ProxyWireTest.ConnectAsync(bridge, deadline.Token);
        await ProxyWireTest.SendAsync(client.GetStream(), ProxyWireTest.ConnectRequest(bridge), deadline.Token);
        var response = await ProxyWireTest.HeadAsync(client.GetStream(), deadline.Token);
        Assert.Equal(502, response.StatusCode);
        Assert.False(receivedHttp);
    }
}
