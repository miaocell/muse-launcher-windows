using System.Globalization;
using System.Text;

namespace MuseLauncher.Core;

internal sealed class ProxyHeaderLimitException : IOException { }

internal sealed record ProxyHttpHead(string StartLine, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Remainder)
{
    public string[] Values(string name) => Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(h => h.Value).ToArray();

    public int StatusCode
    {
        get
        {
            var pieces = StartLine.Split(' ', 3);
            if (pieces.Length < 2 || pieces[0] is not ("HTTP/1.0" or "HTTP/1.1")
                || pieces[1].Length != 3 || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                || code is < 100 or > 599)
                throw new IOException("Invalid upstream HTTP response.");
            return code;
        }
    }
}

internal readonly record struct ProxyHttpRequest(string Method, string Target, ProxyEndpoint Destination, Uri? AbsoluteUri,
    bool WebSocketUpgrade, ProxyBodyFraming Body, bool ExpectContinue)
{
    public static ProxyHttpRequest Parse(ProxyHttpHead head)
    {
        var parts = head.StartLine.Split(' ');
        if (parts.Length != 3 || !ProxyHttpProtocol.IsToken(parts[0]) || parts[2] is not ("HTTP/1.0" or "HTTP/1.1")
            || parts[1].Any(c => c <= 32 || c > 126))
            throw new FormatException("Invalid HTTP request line.");
        var body = ProxyBodyFraming.Parse(head);
        var expect = head.Values("Expect");
        if (expect.Length > 1 || expect.Any(x => !x.Equals("100-continue", StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("Unsupported HTTP expectation.");
        if (parts[0] == "CONNECT")
        {
            if (body.Kind == ProxyBodyKind.Chunked || body.Length > 0) throw new FormatException("CONNECT cannot contain a body.");
            return new(parts[0], parts[1], ProxyEndpoint.Parse(parts[1]), null, false, body, false);
        }
        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new FormatException("Only absolute-form HTTP requests are supported.");
        var authorityStart = parts[1].IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) throw new FormatException("Invalid HTTP destination.");
        var rest = parts[1][(authorityStart + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var destination = ProxyEndpoint.Parse(end < 0 ? rest : rest[..end], 80);
        var upgrade = head.Values("Upgrade");
        var websocket = upgrade.Length == 1 && upgrade[0].Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && ProxyHttpProtocol.ConnectionTokens(head).Contains("upgrade");
        if (websocket && (parts[0] != "GET" || body.Kind == ProxyBodyKind.Chunked || body.Length > 0))
            throw new FormatException("Invalid WebSocket upgrade request.");
        return new(parts[0], parts[1], destination, uri, websocket, body, expect.Length == 1);
    }
}

internal enum ProxyBodyKind { None, ContentLength, Chunked }

internal readonly record struct ProxyBodyFraming(ProxyBodyKind Kind, long Length)
{
    public static ProxyBodyFraming Parse(ProxyHttpHead head)
    {
        var lengths = head.Values("Content-Length");
        var encodings = head.Values("Transfer-Encoding");
        if (lengths.Length > 1 || encodings.Length > 1 || (lengths.Length != 0 && encodings.Length != 0))
            throw new FormatException("Ambiguous HTTP body framing.");
        if (lengths.Length == 1)
        {
            if (!long.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                throw new FormatException("Invalid HTTP content length.");
            return new(ProxyBodyKind.ContentLength, length);
        }
        if (encodings.Length == 1)
        {
            if (!encodings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Unsupported HTTP transfer encoding.");
            return new(ProxyBodyKind.Chunked, 0);
        }
        return new(ProxyBodyKind.None, 0);
    }
}

internal static class ProxyHttpProtocol
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate", "Keep-Alive", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Expect",
    };

    public static bool IsToken(string value) => value.Length != 0 && value.All(c => char.IsAsciiLetterOrDigit(c)
        || "!#$%&'*+-.^_`|~".Contains(c));

    public static HashSet<string> ConnectionTokens(ProxyHttpHead head)
        => head.Values("Connection").SelectMany(v => v.Split(',')).Select(v => v.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static async Task<ProxyHttpHead> ReadHeadAsync(Stream stream, int limit, CancellationToken token)
    {
        var data = new byte[limit];
        var count = 0;
        while (count < limit)
        {
            var previous = count;
            var n = await stream.ReadAsync(data.AsMemory(count, Math.Min(4096, limit - count)), token).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("HTTP headers were incomplete.");
            count += n;
            for (var i = Math.Max(0, previous - 3); i + 3 < count; i++)
            {
                if (data[i] != 13 || data[i + 1] != 10 || data[i + 2] != 13 || data[i + 3] != 10) continue;
                var text = Encoding.Latin1.GetString(data, 0, i);
                var lines = text.Split("\r\n", StringSplitOptions.None);
                if (lines[0].Length == 0 || lines[0].Any(c => c < 32 || c > 126))
                    throw new FormatException("Invalid HTTP start line.");
                var headers = new List<KeyValuePair<string, string>>(lines.Length - 1);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0 || !IsToken(line[..colon]) || line[(colon + 1)..].Any(c => (c < 32 && c != '\t') || c == 127))
                        throw new FormatException("Invalid HTTP header.");
                    headers.Add(new(line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
                }
                return new(lines[0], headers, data[(i + 4)..count]);
            }
        }
        throw new ProxyHeaderLimitException();
    }

    public static string BuildRequest(ProxyHttpHead head, ProxyHttpRequest request, bool absoluteForm, string? upstreamAuthorization)
    {
        var target = absoluteForm ? request.AbsoluteUri!.AbsoluteUri : request.AbsoluteUri!.PathAndQuery;
        var output = new StringBuilder(request.Method + " " + target + " HTTP/1.1\r\n");
        AppendEndToEndHeaders(output, head, skipHost: true);
        output.Append("Host: ").Append(request.Destination.Authority).Append("\r\n");
        AppendFraming(output, request.Body);
        if (request.WebSocketUpgrade) output.Append("Connection: Upgrade\r\nUpgrade: websocket\r\n");
        else output.Append("Connection: close\r\n");
        if (upstreamAuthorization is not null) output.Append("Proxy-Authorization: ").Append(upstreamAuthorization).Append("\r\n");
        return output.Append("\r\n").ToString();
    }

    public static string BuildResponse(ProxyHttpHead head, ProxyBodyFraming framing, bool websocket)
    {
        var output = new StringBuilder(head.StartLine + "\r\n");
        AppendEndToEndHeaders(output, head, skipHost: false);
        if (websocket) output.Append("Connection: Upgrade\r\nUpgrade: websocket\r\n");
        else
        {
            AppendFraming(output, framing);
            output.Append("Connection: close\r\n");
        }
        return output.Append("\r\n").ToString();
    }

    private static void AppendEndToEndHeaders(StringBuilder output, ProxyHttpHead head, bool skipHost)
    {
        var nominated = ConnectionTokens(head);
        // Connection cannot hide framing or an authentication header: parsing/authentication happens before stripping.
        foreach (var field in head.Headers)
        {
            if (HopHeaders.Contains(field.Key) || nominated.Contains(field.Key)
                || (skipHost && field.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))) continue;
            output.Append(field.Key).Append(": ").Append(field.Value).Append("\r\n");
        }
    }

    private static void AppendFraming(StringBuilder output, ProxyBodyFraming framing)
    {
        if (framing.Kind == ProxyBodyKind.ContentLength)
            output.Append("Content-Length: ").Append(framing.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        else if (framing.Kind == ProxyBodyKind.Chunked) output.Append("Transfer-Encoding: chunked\r\n");
    }

    public static Task WriteAsync(Stream stream, string text, CancellationToken token)
        => stream.WriteAsync(Encoding.Latin1.GetBytes(text), token).AsTask();

    public static Task SendErrorAsync(Stream stream, int status, string reason, CancellationToken token, string? challenge = null)
        => WriteAsync(stream, $"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n"
            + (challenge is null ? "" : "Proxy-Authenticate: " + challenge + "\r\n") + "\r\n", token);

    public static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken token)
    {
        var buffer = new byte[32 * 1024];
        while (length > 0)
        {
            var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("HTTP body was incomplete.");
            await destination.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
            length -= n;
        }
    }

    public static async Task CopyBodyAsync(Stream source, Stream destination, ProxyBodyFraming framing, bool toEnd,
        int lineLimit, CancellationToken token)
    {
        if (framing.Kind == ProxyBodyKind.ContentLength)
            await CopyExactlyAsync(source, destination, framing.Length, token).ConfigureAwait(false);
        else if (framing.Kind == ProxyBodyKind.Chunked)
            await CopyChunkedAsync(source, destination, lineLimit, token).ConfigureAwait(false);
        else if (toEnd) await source.CopyToAsync(destination, 32 * 1024, token).ConfigureAwait(false);
    }

    private static async Task CopyChunkedAsync(Stream source, Stream destination, int lineLimit, CancellationToken token)
    {
        while (true)
        {
            var line = await ReadLineAsync(source, lineLimit, token).ConfigureAwait(false);
            var semicolon = line.IndexOf(';');
            var sizePart = semicolon < 0 ? line : line[..semicolon];
            if (sizePart.Length is 0 or > 16 || !long.TryParse(sizePart, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new IOException("Invalid HTTP chunk size.");
            if (size == 0)
            {
                // Do not propagate hop-by-hop or credential-bearing trailers. Bound even discarded input.
                var remaining = lineLimit;
                while (true)
                {
                    var trailer = await ReadLineAsync(source, remaining, token).ConfigureAwait(false);
                    remaining -= trailer.Length + 2;
                    if (remaining < 0) throw new ProxyHeaderLimitException();
                    if (trailer.Length == 0) break;
                    var colon = trailer.IndexOf(':');
                    if (colon <= 0 || !IsToken(trailer[..colon])) throw new IOException("Invalid HTTP trailer.");
                }
                await WriteAsync(destination, "0\r\n\r\n", token).ConfigureAwait(false);
                return;
            }
            await WriteAsync(destination, size.ToString("x", CultureInfo.InvariantCulture) + "\r\n", token).ConfigureAwait(false);
            await CopyExactlyAsync(source, destination, size, token).ConfigureAwait(false);
            var ending = new byte[2];
            await source.ReadExactlyAsync(ending, token).ConfigureAwait(false);
            if (ending[0] != 13 || ending[1] != 10) throw new IOException("Invalid HTTP chunk terminator.");
            await destination.WriteAsync(ending, token).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, int limit, CancellationToken token)
    {
        var data = new List<byte>();
        var one = new byte[1];
        while (data.Count + 2 <= limit)
        {
            await stream.ReadExactlyAsync(one, token).ConfigureAwait(false);
            if (one[0] == 13)
            {
                await stream.ReadExactlyAsync(one, token).ConfigureAwait(false);
                if (one[0] != 10) throw new IOException("Invalid HTTP line ending.");
                return Encoding.Latin1.GetString(data.ToArray());
            }
            if (one[0] == 10 || one[0] < 32 || one[0] == 127) throw new IOException("Invalid HTTP line.");
            data.Add(one[0]);
        }
        throw new ProxyHeaderLimitException();
    }
}

/// <summary>Preserves bytes coalesced with a header or handshake, without taking ownership of the transport.</summary>
internal sealed class ProxyPrefixedStream(Stream inner, byte[] prefix) : Stream
{
    private int offset;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (this.offset == prefix.Length) return inner.Read(buffer, offset, count);
        var n = Math.Min(count, prefix.Length - this.offset);
        prefix.AsSpan(this.offset, n).CopyTo(buffer.AsSpan(offset, n));
        this.offset += n;
        return n;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset == prefix.Length) return inner.ReadAsync(buffer, cancellationToken);
        var n = Math.Min(buffer.Length, prefix.Length - offset);
        prefix.AsMemory(offset, n).CopyTo(buffer);
        offset += n;
        return ValueTask.FromResult(n);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
