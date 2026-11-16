using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MuseLauncher.Core;

internal sealed class ProxyConnection(TcpClient client, Stream stream) : IAsyncDisposable
{
    private readonly Stream ownedTransport = stream;
    public TcpClient Client { get; } = client;
    public Stream Stream { get; set; } = stream;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await ownedTransport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Every TCP connection is to the configured upstream. No destination or direct fallback is dialled here.</summary>
internal static class ProxyConnector
{
    public static string? BasicAuthorization(ProxyConfiguration upstream)
        => upstream.HasCredentials ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(upstream.Username + ":" + upstream.Password)) : null;

    public static async Task<ProxyConnection> OpenTransportAsync(ProxyConfiguration upstream, ProxyBridgeOptions options,
        Action<TcpClient> register, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        register(client);
        Stream? stream = null;
        try
        {
            await client.ConnectAsync(upstream.Host, upstream.Port, token).ConfigureAwait(false);
            stream = client.GetStream();
            if (upstream.Protocol == ProxyProtocol.Https)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = tls;
                var authentication = new SslClientAuthenticationOptions { TargetHost = upstream.Host };
                if (options.TrustedRoots is not null)
                {
                    // Internal test seam: trust a specific fixture CA, never accept arbitrary certificates.
                    authentication.CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck,
                    };
                    authentication.CertificateChainPolicy.CustomTrustStore.AddRange(options.TrustedRoots);
                }
                await tls.AuthenticateAsClientAsync(authentication, token).ConfigureAwait(false);
            }
            return new ProxyConnection(client, stream);
        }
        catch
        {
            client.Dispose();
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<ProxyConnection> OpenTunnelAsync(ProxyConfiguration upstream, ProxyEndpoint destination,
        ProxyBridgeOptions options, Action<TcpClient> register, CancellationToken token)
    {
        var connection = await OpenTransportAsync(upstream, options, register, token).ConfigureAwait(false);
        try
        {
            if (upstream.Protocol == ProxyProtocol.Socks5)
            {
                await SocksHandshakeAsync(connection.Stream, upstream, destination, token).ConfigureAwait(false);
            }
            else
            {
                var auth = BasicAuthorization(upstream);
                await ProxyHttpProtocol.WriteAsync(connection.Stream,
                    "CONNECT " + destination.Authority + " HTTP/1.1\r\nHost: " + destination.Authority + "\r\n"
                    + (auth is null ? "" : "Proxy-Authorization: " + auth + "\r\n") + "\r\n", token).ConfigureAwait(false);
                var response = await ReadFinalResponseAsync(connection.Stream, options.HeaderLimit, token).ConfigureAwait(false);
                if (response.StatusCode is < 200 or >= 300)
                    throw new IOException("The upstream proxy rejected the tunnel.");
                connection.Stream = new ProxyPrefixedStream(connection.Stream, response.Remainder);
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<ProxyHttpHead> ReadFinalResponseAsync(Stream stream, int limit, CancellationToken token)
    {
        for (var i = 0; i < 5; i++)
        {
            var response = await ProxyHttpProtocol.ReadHeadAsync(stream, limit, token).ConfigureAwait(false);
            if (response.StatusCode >= 200 || response.StatusCode == 101) return response;
            stream = new ProxyPrefixedStream(stream, response.Remainder);
        }
        throw new IOException("Too many upstream interim responses.");
    }

    private static async Task SocksHandshakeAsync(Stream stream, ProxyConfiguration upstream, ProxyEndpoint destination,
        CancellationToken token)
    {
        var method = (byte)(upstream.HasCredentials ? 2 : 0);
        await stream.WriteAsync(new byte[] { 5, 1, method }, token).ConfigureAwait(false);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
        if (greeting[0] != 5 || greeting[1] != method)
            throw new IOException("The SOCKS5 proxy rejected the authentication method.");
        if (upstream.HasCredentials)
        {
            var user = Encoding.UTF8.GetBytes(upstream.Username!);
            var password = Encoding.UTF8.GetBytes(upstream.Password!);
            if (user.Length is < 1 or > 255 || password.Length is < 1 or > 255)
                throw new IOException("Invalid SOCKS5 credential length.");
            var authentication = new byte[3 + user.Length + password.Length];
            authentication[0] = 1;
            authentication[1] = (byte)user.Length;
            user.CopyTo(authentication, 2);
            authentication[2 + user.Length] = (byte)password.Length;
            password.CopyTo(authentication, 3 + user.Length);
            await stream.WriteAsync(authentication, token).ConfigureAwait(false);
            var status = new byte[2];
            await stream.ReadExactlyAsync(status, token).ConfigureAwait(false);
            if (status[0] != 1 || status[1] != 0) throw new IOException("SOCKS5 proxy authentication failed.");
        }

        using var request = new MemoryStream();
        request.Write(new byte[] { 5, 1, 0 });
        if (IPAddress.TryParse(destination.Host, out var ip))
        {
            request.WriteByte(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            request.Write(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.UTF8.GetBytes(destination.Host);
            if (name.Length is < 1 or > 255) throw new IOException("SOCKS5 destination hostname is too long.");
            request.WriteByte(3);
            request.WriteByte((byte)name.Length);
            request.Write(name);
        }
        request.WriteByte((byte)(destination.Port >> 8));
        request.WriteByte((byte)destination.Port);
        await stream.WriteAsync(request.ToArray(), token).ConfigureAwait(false);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (header[0] != 5 || header[1] != 0 || header[2] != 0)
            throw new IOException("The SOCKS5 proxy rejected the connection.");
        var addressLength = header[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(stream, token).ConfigureAwait(false),
            _ => throw new IOException("Invalid SOCKS5 reply address type."),
        };
        if (addressLength == 0) throw new IOException("Invalid SOCKS5 reply address.");
        await stream.ReadExactlyAsync(new byte[addressLength + 2], token).ConfigureAwait(false);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        return length[0];
    }
}
