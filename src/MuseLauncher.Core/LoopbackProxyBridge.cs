using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace MuseLauncher.Core;

/// <summary>
/// A per-account, authenticated loopback proxy. Only this bridge holds upstream credentials.
/// It connects exclusively to the configured upstream and never falls back to direct networking.
/// </summary>
public sealed class LoopbackProxyBridge : IAsyncDisposable
{
    public const string AuthenticationRealm = "MuseLauncher Local Proxy";
    private const string AuthenticationChallenge = "Basic realm=\"" + AuthenticationRealm + "\"";

    private readonly ProxyConfiguration upstream;
    private readonly ProxyBridgeOptions options;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown;
    private readonly CancellationTokenRegistration stopRegistration;
    private readonly object gate = new();
    private readonly HashSet<BridgeConnection> connections = [];
    private readonly byte[] localCredentialsHash;
    private readonly Task completion;
    private Task? disposal;
    private bool stopping;

    public Uri Address { get; }
    public string Username { get; }
    public string Password { get; }
    public int ActiveConnections { get { lock (gate) return connections.Count; } }
    /// <summary>Completes once listening and all accepted client/upstream tasks have stopped.</summary>
    public Task Completion => completion;

    private LoopbackProxyBridge(ProxyConfiguration upstream, ProxyBridgeOptions options)
    {
        this.upstream = upstream;
        this.options = options;
        Username = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        localCredentialsHash = SHA256.HashData(Encoding.UTF8.GetBytes(Username + ":" + Password));
        shutdown = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(options.MaxConnections);
        Address = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        stopRegistration = shutdown.Token.Register(StopSockets);
        completion = AcceptLoopAsync();
    }

    /// <summary>The cancellation token controls startup only. DisposeAsync owns the bridge lifetime after success.</summary>
    public static Task<LoopbackProxyBridge> StartAsync(ProxyConfiguration upstream, CancellationToken cancellationToken = default)
        => StartAsync(upstream, new ProxyBridgeOptions(), cancellationToken);

    internal static async Task<LoopbackProxyBridge> StartAsync(ProxyConfiguration upstream, ProxyBridgeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        // Binding a local listener is the only work performed at startup; no upstream DNS/connect/auth occurs.
        var bridge = new LoopbackProxyBridge(upstream, options);
        if (cancellationToken.IsCancellationRequested)
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return bridge;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(shutdown.Token).ConfigureAwait(false);
                client.NoDelay = true;
                BridgeConnection connection;
                lock (gate)
                {
                    // Reject before allocating per-client tasks when the bounded capacity is exhausted.
                    if (stopping || connections.Count >= options.MaxConnections)
                    {
                        client.Dispose();
                        continue;
                    }
                    connection = new BridgeConnection(client);
                    connections.Add(connection);
                }
                _ = RunConnectionAsync(connection);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!shutdown.IsCancellationRequested) throw;
        }
        finally
        {
            StopSockets();
            Task[] active;
            lock (gate) active = connections.Select(c => c.Finished.Task).ToArray();
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task RunConnectionAsync(BridgeConnection connection)
    {
        Stream? stream = null;
        var responseStarted = false;
        var requestValidated = false;
        try
        {
            stream = connection.Client.GetStream();
            ProxyHttpHead head;
            using (var deadline = Deadline(options.HeaderTimeout))
                head = await ProxyHttpProtocol.ReadHeadAsync(stream, options.HeaderLimit, deadline.Token).ConfigureAwait(false);
            if (!IsAuthorized(head))
            {
                await SendErrorAsync(stream, 407, "Proxy Authentication Required", AuthenticationChallenge).ConfigureAwait(false);
                return;
            }
            var request = ProxyHttpRequest.Parse(head);
            requestValidated = true;
            if (request.Method == "CONNECT")
            {
                ProxyConnection tunnel;
                using (var deadline = Deadline(options.ConnectTimeout))
                    tunnel = await ProxyConnector.OpenTunnelAsync(upstream, request.Destination, options,
                        connection.AddUpstream, deadline.Token).ConfigureAwait(false);
                await using (tunnel)
                {
                    await ProxyHttpProtocol.WriteAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", shutdown.Token).ConfigureAwait(false);
                    responseStarted = true;
                    await RelayTunnelAsync(connection, tunnel, new ProxyPrefixedStream(stream, head.Remainder), shutdown.Token).ConfigureAwait(false);
                }
            }
            else
            {
                await ForwardHttpAsync(connection, stream, head, request, () => responseStarted = true).ConfigureAwait(false);
            }
        }
        catch (ProxyHeaderLimitException)
        {
            if (!responseStarted && stream is not null)
                await SendErrorAsync(stream, requestValidated ? 502 : 431, requestValidated ? "Bad Gateway" : "Request Header Fields Too Large").ConfigureAwait(false);
        }
        catch (FormatException)
        {
            if (!responseStarted && stream is not null)
                await SendErrorAsync(stream, requestValidated ? 502 : 400, requestValidated ? "Bad Gateway" : "Bad Request").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!responseStarted && stream is not null && !shutdown.IsCancellationRequested)
                await SendErrorAsync(stream, 504, "Gateway Timeout").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            // Never propagate an upstream 407, realm, body, exception or credentials to the browser.
            if (!responseStarted && stream is not null && !shutdown.IsCancellationRequested)
                await SendErrorAsync(stream, 502, "Bad Gateway").ConfigureAwait(false);
        }
        finally
        {
            connection.Close();
            lock (gate)
            {
                connections.Remove(connection);
                connection.Finished.TrySetResult();
            }
        }
    }

    private bool IsAuthorized(ProxyHttpHead head)
    {
        var authorization = head.Values("Proxy-Authorization");
        if (authorization.Length != 1) return false;
        var value = authorization[0];
        var space = value.IndexOf(' ');
        if (space < 0 || !value[..space].Equals("Basic", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var credentials = Convert.FromBase64String(value[(space + 1)..].Trim());
            var suppliedHash = SHA256.HashData(credentials);
            return CryptographicOperations.FixedTimeEquals(localCredentialsHash, suppliedHash);
        }
        catch (FormatException) { return false; }
    }

    private async Task ForwardHttpAsync(BridgeConnection client, Stream downstream, ProxyHttpHead head,
        ProxyHttpRequest request, Action responseStarted)
    {
        ProxyConnection transport;
        using (var deadline = Deadline(options.ConnectTimeout))
        {
            transport = upstream.Protocol == ProxyProtocol.Socks5
                ? await ProxyConnector.OpenTunnelAsync(upstream, request.Destination, options, client.AddUpstream, deadline.Token).ConfigureAwait(false)
                : await ProxyConnector.OpenTransportAsync(upstream, options, client.AddUpstream, deadline.Token).ConfigureAwait(false);
            try
            {
                var directToDestinationThroughSocks = upstream.Protocol == ProxyProtocol.Socks5;
                await ProxyHttpProtocol.WriteAsync(transport.Stream,
                    ProxyHttpProtocol.BuildRequest(head, request, !directToDestinationThroughSocks,
                        directToDestinationThroughSocks ? null : ProxyConnector.BasicAuthorization(upstream)), deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        await using (transport)
        {
            if (request.ExpectContinue)
                await ProxyHttpProtocol.WriteAsync(downstream, "HTTP/1.1 100 Continue\r\n\r\n", shutdown.Token).ConfigureAwait(false);
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            var upload = ProxyHttpProtocol.CopyBodyAsync(new ProxyPrefixedStream(downstream, head.Remainder), transport.Stream,
                request.Body, false, options.HeaderLimit, exchange.Token);
            try
            {
                ProxyHttpHead response;
                using (var deadline = Deadline(options.HeaderTimeout))
                    response = await ProxyConnector.ReadFinalResponseAsync(transport.Stream, options.HeaderLimit, deadline.Token).ConfigureAwait(false);
                var status = response.StatusCode;
                if (status == 407) throw new IOException("Upstream authentication failed.");
                var framing = ProxyBodyFraming.Parse(response);
                var websocket = status == 101 && request.WebSocketUpgrade
                    && response.Values("Upgrade") is [var upgrade] && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);
                if (status == 101 && !websocket) throw new IOException("Unsupported upstream protocol upgrade.");
                await ProxyHttpProtocol.WriteAsync(downstream, ProxyHttpProtocol.BuildResponse(response, framing, websocket), shutdown.Token).ConfigureAwait(false);
                responseStarted();
                var source = new ProxyPrefixedStream(transport.Stream, response.Remainder);
                if (websocket)
                {
                    await upload.ConfigureAwait(false);
                    transport.Stream = source;
                    await RelayTunnelAsync(client, transport, new ProxyPrefixedStream(downstream, head.Remainder), shutdown.Token).ConfigureAwait(false);
                }
                else if (request.Method != "HEAD" && status is not (204 or 304))
                {
                    await ProxyHttpProtocol.CopyBodyAsync(source, downstream, framing, true, options.HeaderLimit, exchange.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                exchange.Cancel();
                try { await upload.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }
    }

    private static async Task RelayTunnelAsync(BridgeConnection client, ProxyConnection upstreamConnection, Stream downstream,
        CancellationToken token)
    {
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
        var upload = PumpAsync(downstream, upstreamConnection.Stream, upstreamConnection.Client, relay.Token);
        var download = PumpAsync(upstreamConnection.Stream, downstream, client.Client, relay.Token);
        var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
        if (first.IsFaulted || first.IsCanceled) relay.Cancel();
        try { await Task.WhenAll(upload, download).ConfigureAwait(false); }
        finally { relay.Cancel(); }
    }

    private static async Task PumpAsync(Stream source, Stream destination, TcpClient destinationClient, CancellationToken token)
    {
        await source.CopyToAsync(destination, 32 * 1024, token).ConfigureAwait(false);
        await destination.FlushAsync(token).ConfigureAwait(false);
        try { destinationClient.Client.Shutdown(SocketShutdown.Send); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private CancellationTokenSource Deadline(TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private async Task SendErrorAsync(Stream stream, int status, string reason, string? challenge = null)
    {
        using var deadline = Deadline(options.HeaderTimeout);
        try { await ProxyHttpProtocol.SendErrorAsync(stream, status, reason, deadline.Token, challenge).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void StopSockets()
    {
        lock (gate)
        {
            stopping = true;
            listener.Stop();
            foreach (var connection in connections) connection.Close();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (gate)
        {
            disposal ??= DisposeCoreAsync();
            task = disposal;
        }
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        shutdown.Cancel();
        StopSockets();
        try { await completion.ConfigureAwait(false); }
        finally
        {
            stopRegistration.Dispose();
            shutdown.Dispose();
        }
    }

    private sealed class BridgeConnection(TcpClient client)
    {
        private readonly object connectionGate = new();
        private readonly List<TcpClient> upstreamSockets = [];
        private bool closed;
        public TcpClient Client { get; } = client;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AddUpstream(TcpClient socket)
        {
            lock (connectionGate)
            {
                if (closed) { socket.Dispose(); throw new OperationCanceledException(); }
                upstreamSockets.Add(socket);
            }
        }

        public void Close()
        {
            lock (connectionGate)
            {
                closed = true;
                Client.Dispose();
                foreach (var socket in upstreamSockets) socket.Dispose();
            }
        }
    }
}
