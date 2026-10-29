using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Hushwire.Core.AdFilter;

/// <summary>
/// Local HTTPS server for a handful of YouTube/Twitch hostnames. Hushwire's DNS answers point those names here; this server
/// presents a certificate from <see cref="LocalCa"/>, forwards each request to the real site, and removes ad data from the
/// responses that carry it. Everything else is relayed untouched. Never listens beyond loopback.
/// </summary>
public sealed class MitmServer : IDisposable
{
    public int Port { get; }
    public bool Running { get; private set; }
    public bool Ipv6Bound { get; private set; }
    /// <summary>(site, removed items) for each rewritten response.</summary>
    public Action<string, int>? OnRewritten { get; init; }
    public Action<string>? OnError { get; init; }
    /// <summary>Port of the real site; only tests change it (the constructor takes a handler factory too, so tests can trust a fake origin).</summary>
    public int UpstreamPort { get; }

    const int MaxHead = 64 * 1024, MaxRequestBody = 8 * 1024 * 1024, MaxRewriteBody = 16 * 1024 * 1024;

    readonly LocalCa ca;
    readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;
    readonly CancellationTokenSource cts = new();
    readonly List<TcpListener> listeners = [];
    readonly HttpClient http;
    readonly SemaphoreSlim conns = new(256);

    public MitmServer(LocalCa ca, Func<string, CancellationToken, Task<IPAddress[]>> realResolver, int port = 443, int upstreamPort = 443,
        Func<Func<string, CancellationToken, Task<IPAddress[]>>, HttpMessageHandler>? handlerFactory = null)
    {
        this.ca = ca; resolve = realResolver; Port = port; UpstreamPort = upstreamPort;
        http = new HttpClient(handlerFactory?.Invoke(resolve) ?? DefaultHandler(resolve), true) { Timeout = TimeSpan.FromSeconds(60) };
    }

    static HttpMessageHandler DefaultHandler(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = (ctx, ct) => ConnectAsync(resolve, ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct),
    };

    /// <summary>Connects using the real (not redirected) addresses, otherwise the redirect would loop back into this server.</summary>
    public static async ValueTask<Stream> ConnectAsync(Func<string, CancellationToken, Task<IPAddress[]>> resolve, string host, int port, CancellationToken ct)
    {
        var ips = await resolve(host, ct);
        Exception? last = null;
        foreach (var ip in ips)
        {
            var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(new IPEndPoint(ip, port), ct); return new NetworkStream(s, true); }
            catch (Exception e) { s.Dispose(); last = e; }
        }
        throw new HttpRequestException($"could not connect to {host}", last);
    }

    /// <summary>Binds 127.0.0.1 (required) and ::1 (best effort). On failure nothing stays bound.</summary>
    public void Start()
    {
        try
        {
            Listen(IPAddress.Loopback);
            try { Listen(IPAddress.IPv6Loopback); Ipv6Bound = true; } catch (SocketException) { /* IPv4 only */ }
        }
        catch { Dispose(); throw; }
        Running = true;
        foreach (var l in listeners) _ = Task.Run(() => AcceptLoop(l, cts.Token));
    }

    void Listen(IPAddress ip)
    {
        var l = new TcpListener(ip, Port);
        l.Start(128);
        listeners.Add(l);
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = await l.AcceptTcpClientAsync(ct);
                if (!conns.Wait(0)) { c.Dispose(); continue; }
                _ = Task.Run(async () => { try { await Handle(c, ct); } finally { conns.Release(); } }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(20, CancellationToken.None); }
        }
    }

    // ------------------------------------------------------------------ one client connection

    async Task Handle(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        try
        {
            using var ssl = new SslStream(client.GetStream(), false);
            // a connection that never finishes its handshake must not hold one of the 256 slots forever
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, host) =>
                    !string.IsNullOrEmpty(host) && InterceptList.Matches(host) ? ca.Issue(host) : throw new AuthenticationException("host is not filtered"),
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, handshake.Token);
            string sni = ssl.TargetHostName.TrimEnd('.').ToLowerInvariant();
            var conn = new Conn(ssl);
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(90));
                if (!await ServeOne(conn, ssl, sni, idle.Token)) break;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or AuthenticationException or SocketException or ObjectDisposedException or InvalidDataException) { }
        catch (Exception e) { OnError?.Invoke(e.Message); }
    }

    static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "proxy-authenticate", "proxy-authorization", "te", "trailer",
        "transfer-encoding", "upgrade", "host", "accept-encoding", "content-length", "expect",
    };

    /// <summary>Handles one request/response. Returns false when the connection should close.</summary>
    async Task<bool> ServeOne(Conn conn, Stream client, string sni, CancellationToken ct)
    {
        var requestLine = await conn.ReadLineAsync(ct);
        if (requestLine is null) return false;                      // client closed
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/'))
        { await Simple(client, 400, "Bad Request", ct); return false; }
        string method = parts[0], target = parts[1];

        var headers = new List<(string Name, string Value)>();
        int headBytes = 0;
        while (true)
        {
            var line = await conn.ReadLineAsync(ct) ?? throw new InvalidDataException("truncated headers");
            if (line.Length == 0) break;
            if ((headBytes += line.Length) > MaxHead) { await Simple(client, 431, "Request Header Fields Too Large", ct); return false; }
            int c = line.IndexOf(':');
            if (c <= 0) { await Simple(client, 400, "Bad Request", ct); return false; }
            headers.Add((line[..c].Trim(), line[(c + 1)..].Trim()));
        }
        string? H(string n) => headers.FirstOrDefault(h => h.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Value;

        // The Host header must match the name the browser asked the TLS layer for; otherwise this would be an open relay.
        string host = (H("Host") ?? "").Split(':')[0].TrimEnd('.').ToLowerInvariant();
        if (host != sni || !InterceptList.Matches(host)) { await Simple(client, 421, "Misdirected Request", ct); return false; }
        if (H("Upgrade") is not null) { await Simple(client, 501, "Not Implemented", ct); return false; }

        byte[]? body = null;
        if (string.Equals(H("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase)) body = await conn.ReadChunkedAsync(MaxRequestBody, ct);
        else if (long.TryParse(H("Content-Length"), out long len) && len > 0)
        {
            if (len > MaxRequestBody) { await Simple(client, 413, "Payload Too Large", ct); return false; }
            body = await conn.ReadExactAsync((int)len, ct);
        }
        bool clientClose = (H("Connection") ?? "").Contains("close", StringComparison.OrdinalIgnoreCase) || parts[2] == "HTTP/1.0";

        // ---- forward
        string authority = UpstreamPort == 443 ? host : $"{host}:{UpstreamPort}";
        if (!Uri.TryCreate($"https://{authority}{target}", UriKind.Absolute, out var uri)) { await Simple(client, 400, "Bad Request", ct); return false; }
        using var req = new HttpRequestMessage(new HttpMethod(method), uri) { Version = System.Net.HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        if (body is not null) req.Content = new ByteArrayContent(body);
        foreach (var (n, v) in headers)
        {
            if (HopByHop.Contains(n)) continue;
            if (!req.Headers.TryAddWithoutValidation(n, v)) req.Content?.Headers.TryAddWithoutValidation(n, v);
        }

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            await Simple(client, 502, "Bad Gateway (Hushwire could not reach " + host + ")", ct);
            return !clientClose;
        }
        using var _ = resp;

        int status = (int)resp.StatusCode;
        bool noBody = method == "HEAD" || status is 204 or 304 or < 200;
        string? mediaType = resp.Content.Headers.ContentType?.MediaType;
        var kind = status == 200 && !noBody ? InterceptList.Classify(host, target, mediaType) : RewriteKind.None;

        await using Stream? respStream = noBody ? null : await resp.Content.ReadAsStreamAsync(ct);
        byte[]? buffered = null;
        bool overflow = false;
        MemoryStream? prefix = null;
        if (kind != RewriteKind.None)
        {
            prefix = new MemoryStream();
            using var rented = new PooledBuffer();
            var buf = rented.Array;
            int n;
            while ((n = await respStream!.ReadAsync(buf, ct)) > 0)
            {
                prefix.Write(buf, 0, n);
                if (prefix.Length > MaxRewriteBody) { overflow = true; break; }
            }
            if (!overflow) buffered = prefix.ToArray();
            if (buffered is not null)
            {
                string text = Encoding.UTF8.GetString(buffered);
                int removed = 0;
                string rewritten = kind switch
                {
                    RewriteKind.YouTubeJson => AdRewriter.PruneYouTubeJson(text, out removed),
                    RewriteKind.YouTubeHtml => AdRewriter.PruneYouTubeHtml(text, out removed),
                    _ => AdRewriter.StripTwitchAds(text, out removed),
                };
                if (removed > 0)
                {
                    buffered = Encoding.UTF8.GetBytes(rewritten);
                    OnRewritten?.Invoke(kind == RewriteKind.TwitchPlaylist ? "twitch" : "youtube", removed);
                }
            }
        }

        // ---- reply
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(resp.ReasonPhrase ?? "").Append("\r\n");
        void Add(HttpHeaders hs)
        {
            foreach (var h in hs)
            {
                string n = h.Key;
                if (HopByHop.Contains(n) || n.Equals("Alt-Svc", StringComparison.OrdinalIgnoreCase) || n.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in h.Value) sb.Append(n).Append(": ").Append(v).Append("\r\n");
            }
        }
        Add(resp.Headers); Add(resp.Content.Headers);
        long? knownLength = resp.Content.Headers.ContentLength;
        bool chunked = false;
        if (noBody) { if (knownLength is not null) sb.Append("Content-Length: ").Append(knownLength).Append("\r\n"); }
        else if (buffered is not null) sb.Append("Content-Length: ").Append(buffered.Length).Append("\r\n");
        else if (!overflow && knownLength is not null) sb.Append("Content-Length: ").Append(knownLength).Append("\r\n");
        else { sb.Append("Transfer-Encoding: chunked\r\n"); chunked = true; }
        if (clientClose) sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        await client.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), ct);

        if (!noBody)
        {
            if (buffered is not null) await client.WriteAsync(buffered, ct);
            else
            {
                if (overflow) await WriteBody(client, prefix!.ToArray(), chunked, ct);
                using var rented = new PooledBuffer();
                var buf = rented.Array;
                int n;
                while ((n = await respStream!.ReadAsync(buf, ct)) > 0) await WriteBody(client, buf.AsMemory(0, n), chunked, ct);
                if (chunked) await client.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
            }
        }
        await client.FlushAsync(ct);
        return !clientClose;
    }

    static async Task WriteBody(Stream s, ReadOnlyMemory<byte> data, bool chunked, CancellationToken ct)
    {
        if (data.Length == 0) return;
        if (chunked) await s.WriteAsync(Encoding.ASCII.GetBytes(data.Length.ToString("X") + "\r\n"), ct);
        await s.WriteAsync(data, ct);
        if (chunked) await s.WriteAsync("\r\n"u8.ToArray(), ct);
    }

    static async Task Simple(Stream s, int code, string reason, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(reason);
        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    public void Dispose()
    {
        Running = false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var l in listeners) l.Stop();
        http.Dispose();
    }

    /// <summary>A copy buffer borrowed from the shared pool instead of a fresh 80 KB array per response.</summary>
    sealed class PooledBuffer : IDisposable
    {
        public byte[] Array { get; } = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        public void Dispose() => System.Buffers.ArrayPool<byte>.Shared.Return(Array);
    }

    /// <summary>Tiny buffered reader for HTTP/1.1 framing on a stream.</summary>
    sealed class Conn(Stream s)
    {
        readonly byte[] buf = new byte[16 * 1024];
        int pos, len;

        async Task<bool> Fill(CancellationToken ct)
        {
            pos = 0;
            len = await s.ReadAsync(buf, ct);
            return len > 0;
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= len && !await Fill(ct)) return sb.Length == 0 ? null : throw new InvalidDataException("truncated line");
                byte b = buf[pos++];
                if (b == '\n') { if (sb.Length > 0 && sb[^1] == '\r') sb.Length--; return sb.ToString(); }
                sb.Append((char)b);
                if (sb.Length > MaxHead) throw new InvalidDataException("line too long");
            }
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var o = new byte[count];
            int got = 0;
            while (got < count)
            {
                if (pos >= len && !await Fill(ct)) throw new InvalidDataException("truncated body");
                int n = Math.Min(count - got, len - pos);
                Buffer.BlockCopy(buf, pos, o, got, n);
                pos += n; got += n;
            }
            return o;
        }

        public async Task<byte[]> ReadChunkedAsync(int max, CancellationToken ct)
        {
            var ms = new MemoryStream();
            while (true)
            {
                var line = await ReadLineAsync(ct) ?? throw new InvalidDataException("truncated chunk");
                int semi = line.IndexOf(';');
                if (!int.TryParse(semi >= 0 ? line[..semi] : line, System.Globalization.NumberStyles.HexNumber, null, out int size) || size < 0)
                    throw new InvalidDataException("bad chunk size");
                if (size == 0) break;
                if (ms.Length + size > max) throw new InvalidDataException("request body too large");
                ms.Write(await ReadExactAsync(size, ct));
                await ReadLineAsync(ct);                  // CRLF after the chunk
            }
            while (!string.IsNullOrEmpty(await ReadLineAsync(ct))) { } // trailers
            return ms.ToArray();
        }
    }
}
