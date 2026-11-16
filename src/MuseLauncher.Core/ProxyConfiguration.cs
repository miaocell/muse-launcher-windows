using System.Globalization;
using System.Net;
using System.Text;

namespace MuseLauncher.Core;

public enum ProxyProtocol
{
    Http,
    Https,
    Socks5,
}

/// <summary>A validated proxy endpoint and its credentials. String representations never contain credentials.</summary>
public sealed class ProxyConfiguration : IEquatable<ProxyConfiguration>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ProxyProtocol Protocol { get; }
    public string Host { get; }
    public int Port { get; }
    public string? Username { get; }
    public string? Password { get; }
    public bool HasCredentials => Username is not null;
    public Uri ServerUri { get; }
    public string DisplayAddress { get; }

    private ProxyConfiguration(ProxyProtocol protocol, ProxyEndpoint endpoint, string? username, string? password)
    {
        Protocol = protocol;
        Host = endpoint.Host;
        Port = endpoint.Port;
        Username = username;
        Password = password;
        var scheme = protocol switch
        {
            ProxyProtocol.Http => "http",
            ProxyProtocol.Https => "https",
            _ => "socks5",
        };
        DisplayAddress = scheme + "://" + endpoint.Authority;
        ServerUri = new Uri(DisplayAddress, UriKind.Absolute);
    }

    /// <summary>Empty input follows the system network. Any non-empty invalid input throws without echoing secrets.</summary>
    public static ProxyConfiguration? Parse(string? address)
    {
        if (address is null) return null;
        if (address.Any(char.IsControl)) throw Invalid("Proxy addresses cannot contain control characters.");
        var value = address.Trim();
        if (value.Length == 0) return null;
        if (value.Length > 8192) throw Invalid("Proxy address is too long.");
        if (value.Any(char.IsWhiteSpace)) throw Invalid("Whitespace must be percent-encoded in proxy credentials.");

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? "http" : value[..schemeEnd].ToLowerInvariant();
        var authority = schemeEnd < 0 ? value : value[(schemeEnd + 3)..];
        var protocol = scheme switch
        {
            "http" => ProxyProtocol.Http,
            "https" => ProxyProtocol.Https,
            "socks5" => ProxyProtocol.Socks5,
            _ => throw Invalid("Supported proxy protocols are http, https and socks5."),
        };
        if (authority.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw Invalid("A proxy address cannot contain a path, query or fragment.");

        string? username = null;
        string? password = null;
        var at = authority.IndexOf('@');
        if (at >= 0)
        {
            if (authority.LastIndexOf('@') != at)
                throw Invalid("An at sign in proxy credentials must be percent-encoded.");
            var userInfo = authority[..at];
            authority = authority[(at + 1)..];
            var colon = userInfo.IndexOf(':');
            username = DecodeCredential(colon < 0 ? userInfo : userInfo[..colon]);
            password = DecodeCredential(colon < 0 ? "" : userInfo[(colon + 1)..]);
            if (username.Length == 0) throw Invalid("Proxy username cannot be empty.");
            if (protocol != ProxyProtocol.Socks5 && username.Contains(':'))
                throw Invalid("A Basic authentication username cannot contain a colon.");
            if (protocol == ProxyProtocol.Socks5 &&
                (StrictUtf8.GetByteCount(username) is < 1 or > 255 || StrictUtf8.GetByteCount(password) is < 1 or > 255))
                throw Invalid("SOCKS5 usernames and passwords must each contain 1 to 255 UTF-8 bytes.");
        }

        var defaultPort = protocol switch
        {
            ProxyProtocol.Http => 80,
            ProxyProtocol.Https => 443,
            _ => 1080,
        };
        return new ProxyConfiguration(protocol, ProxyEndpoint.Parse(authority, defaultPort), username, password);
    }

    public static bool TryParse(string? address, out ProxyConfiguration? configuration, out string? error)
    {
        try
        {
            configuration = Parse(address);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            configuration = null;
            error = ex.Message;
            return false;
        }
    }

    private static string DecodeCredential(string value)
    {
        var result = new StringBuilder();
        try
        {
            for (var i = 0; i < value.Length;)
            {
                if (value[i] != '%')
                {
                    var c = value[i++];
                    if (!(char.IsLetterOrDigit(c) || c > 127 || "-._~!$&'()*+,;=:".Contains(c)))
                        throw Invalid("Reserved characters in credentials must be percent-encoded.");
                    result.Append(c);
                    continue;
                }
                var bytes = new List<byte>();
                while (i < value.Length && value[i] == '%')
                {
                    if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out var b))
                        throw Invalid("Invalid percent-encoding in proxy credentials.");
                    bytes.Add(b);
                    i += 3;
                }
                result.Append(StrictUtf8.GetString(bytes.ToArray()));
            }
            var decoded = result.ToString();
            _ = StrictUtf8.GetByteCount(decoded);
            if (decoded.Any(char.IsControl)) throw Invalid("Proxy credentials cannot contain control characters.");
            return decoded;
        }
        catch (DecoderFallbackException) { throw Invalid("Proxy credentials contain invalid UTF-8."); }
        catch (EncoderFallbackException) { throw Invalid("Proxy credentials contain invalid Unicode."); }
    }

    private static FormatException Invalid(string message) => new(message);

    public bool Equals(ProxyConfiguration? other) => other is not null && Protocol == other.Protocol && Port == other.Port
        && string.Equals(Host, other.Host, StringComparison.Ordinal)
        && string.Equals(Username, other.Username, StringComparison.Ordinal)
        && string.Equals(Password, other.Password, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ProxyConfiguration other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Protocol, Host, Port, Username, Password);
    public static bool operator ==(ProxyConfiguration? left, ProxyConfiguration? right) => Equals(left, right);
    public static bool operator !=(ProxyConfiguration? left, ProxyConfiguration? right) => !Equals(left, right);
    public override string ToString() => DisplayAddress;
}

internal readonly record struct ProxyEndpoint(string Host, int Port)
{
    public string Authority => (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

    public static ProxyEndpoint Parse(string authority, int? defaultPort = null)
    {
        if (string.IsNullOrEmpty(authority) || authority.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || authority.IndexOfAny(['/', '\\', '?', '#', '@', '%', ',', ';', '=', '"']) >= 0)
            throw new FormatException("Invalid proxy host or port.");
        string host;
        string? port = null;
        if (authority[0] == '[')
        {
            var end = authority.IndexOf(']');
            if (end <= 1) throw new FormatException("Invalid IPv6 proxy address.");
            host = authority[1..end];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                throw new FormatException("Invalid IPv6 proxy address.");
            if (end + 1 < authority.Length)
            {
                if (authority[end + 1] != ':') throw new FormatException("Invalid proxy port.");
                port = authority[(end + 2)..];
            }
            host = ip.ToString();
        }
        else
        {
            var colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                if (authority.LastIndexOf(':') != colon) throw new FormatException("IPv6 addresses must be bracketed.");
                host = authority[..colon];
                port = authority[(colon + 1)..];
            }
            else host = authority;
            host = CanonicalizeHost(host);
        }
        var number = defaultPort ?? 0;
        if (port is not null && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
            throw new FormatException("Proxy port must be an integer between 1 and 65535.");
        if (number is < 1 or > 65535) throw new FormatException("Proxy port must be between 1 and 65535.");
        return new ProxyEndpoint(host, number);
    }

    private static string CanonicalizeHost(string host)
    {
        if (host.Length == 0) throw new FormatException("Proxy host cannot be empty.");
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new FormatException("IPv6 addresses must be bracketed.");
            return ip.ToString();
        }
        try { host = new IdnMapping().GetAscii(host).TrimEnd('.').ToLowerInvariant(); }
        catch (ArgumentException) { throw new FormatException("Invalid proxy hostname."); }
        if (host.Length is 0 or > 253 || host.All(c => c == '.' || char.IsAsciiDigit(c)))
            throw new FormatException("Invalid proxy hostname.");
        foreach (var label in host.Split('.'))
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                throw new FormatException("Invalid proxy hostname.");
        return host;
    }
}
