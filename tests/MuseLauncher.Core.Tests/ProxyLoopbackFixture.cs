using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MuseLauncher.Core;

namespace MuseLauncher.Core.Tests;

/// <summary>Independent loopback-only listeners. Handler errors are surfaced after tests rather than swallowed.</summary>
internal sealed class ProxyLoopbackFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly ConcurrentQueue<Task> tasks = new();
    private readonly Func<TcpClient, CancellationToken, Task> handler;
    private readonly Task accepting;
    private int accepted;
    public int Port { get; }
    public int Accepted => Volatile.Read(ref accepted);
    public Uri HttpAddress => new("http://127.0.0.1:" + Port);

    public ProxyLoopbackFixture(Func<TcpClient, CancellationToken, Task> handler)
    {
        this.handler = handler;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref accepted);
                clients.TryAdd(client, 0);
                tasks.Enqueue(HandleAsync(client));
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try { await handler(client, stop.Token); }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        finally { clients.TryRemove(client, out _); client.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        await accepting;
        await Task.WhenAll(tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
        stop.Dispose();
    }
}

internal sealed class ProxyTestCertificates : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }
    public ProxyTestCertificates()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Muse Proxy Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(names.Build());
        using var publicServer = serverRequest.Create(Root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = publicServer.CopyWithPrivateKey(serverKey);
        Server = new X509Certificate2(withKey.Export(X509ContentType.Pfx));
    }
    public void Dispose() { Server.Dispose(); Root.Dispose(); }
}

internal static class ProxyWireTest
{
    public static string Basic(string username, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
    public static string LocalBasic(LoopbackProxyBridge bridge) => Basic(bridge.Username, bridge.Password);

    public static ProxyConfiguration Configuration(ProxyLoopbackFixture fixture, string protocol = "http", string? user = "upstream-user", string password = "upstream-password")
        => ProxyConfiguration.Parse(protocol + "://" + (user is null ? "" : Uri.EscapeDataString(user) + ":" + Uri.EscapeDataString(password) + "@")
            + "127.0.0.1:" + fixture.Port)!;

    public static async Task<TcpClient> ConnectAsync(LoopbackProxyBridge bridge, CancellationToken token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridge.Address.Port, token);
        return client;
    }

    public static Task SendAsync(Stream stream, string text, CancellationToken token)
        => stream.WriteAsync(Encoding.Latin1.GetBytes(text), token).AsTask();

    public static async Task<ProxyHttpHead> HeadAsync(Stream stream, CancellationToken token)
        => await ProxyHttpProtocol.ReadHeadAsync(stream, 32768, token);

    public static string ConnectRequest(LoopbackProxyBridge bridge, string target = "target.invalid:443", string? authorization = null)
        => "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\nProxy-Authorization: "
            + (authorization ?? LocalBasic(bridge)) + "\r\n\r\n";

    public static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken token)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, token);
        return memory.ToArray();
    }

    public static async Task EchoAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(buffer, token)) != 0)
            await stream.WriteAsync(buffer.AsMemory(0, n), token);
    }

    public static async Task<SslStream> ServerTlsAsync(TcpClient client, X509Certificate2 certificate, CancellationToken token)
    {
        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    public static async Task<string> SocksRequestAsync(Stream stream, bool credentials, string user, string password,
        CancellationToken token, bool acceptAuthentication = true)
    {
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, token);
        Xunit.Assert.Equal(new byte[] { 5, 1, (byte)(credentials ? 2 : 0) }, greeting);
        await stream.WriteAsync(new byte[] { 5, greeting[2] }, token);
        if (credentials)
        {
            var header = new byte[2];
            await stream.ReadExactlyAsync(header, token);
            Xunit.Assert.Equal(1, header[0]);
            var username = new byte[header[1]];
            await stream.ReadExactlyAsync(username, token);
            var length = new byte[1];
            await stream.ReadExactlyAsync(length, token);
            var secret = new byte[length[0]];
            await stream.ReadExactlyAsync(secret, token);
            Xunit.Assert.Equal(user, Encoding.UTF8.GetString(username));
            Xunit.Assert.Equal(password, Encoding.UTF8.GetString(secret));
            await stream.WriteAsync(new byte[] { 1, (byte)(acceptAuthentication ? 0 : 1) }, token);
            if (!acceptAuthentication) return "";
        }
        var connect = new byte[4];
        await stream.ReadExactlyAsync(connect, token);
        Xunit.Assert.Equal(new byte[] { 5, 1, 0 }, connect[..3]);
        string host;
        if (connect[3] == 3)
        {
            var length = new byte[1];
            await stream.ReadExactlyAsync(length, token);
            var domain = new byte[length[0]];
            await stream.ReadExactlyAsync(domain, token);
            host = Encoding.UTF8.GetString(domain);
        }
        else
        {
            var address = new byte[connect[3] == 1 ? 4 : 16];
            await stream.ReadExactlyAsync(address, token);
            host = new IPAddress(address).ToString();
        }
        var port = new byte[2];
        await stream.ReadExactlyAsync(port, token);
        return (host.Contains(':') ? "[" + host + "]" : host) + ":" + ((port[0] << 8) | port[1]);
    }

    public static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }
}
