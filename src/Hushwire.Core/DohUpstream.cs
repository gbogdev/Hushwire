using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Hushwire.Core;

public sealed record DohProvider(string Id, string Name, string Url, IPAddress[] Bootstrap);

/// <summary>
/// Sends lookups to a DNS-over-HTTPS server (RFC 8484) so your ISP and network can't read them. The server is reached by its built-in
/// address (no DNS lookup is needed to find it) and its certificate is checked normally. Any failure returns null and the caller
/// falls back to the ordinary upstream, so a captive portal or VPN can't leave the PC without DNS.
/// </summary>
public sealed class DohUpstream : IDisposable
{
    public static readonly DohProvider[] Providers =
    [
        new("cloudflare", "Cloudflare (1.1.1.1)", "https://cloudflare-dns.com/dns-query", [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1"), IPAddress.Parse("2606:4700:4700::1111")]),
        new("quad9", "Quad9 (9.9.9.9, blocks known malware)", "https://dns.quad9.net/dns-query", [IPAddress.Parse("9.9.9.9"), IPAddress.Parse("149.112.112.112")]),
        new("google", "Google Public DNS", "https://dns.google/dns-query", [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4")]),
    ];

    public static DohProvider? Find(string? id) => Providers.FirstOrDefault(p => p.Id == id);

    const int MaxResponse = 65535;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    readonly HttpClient http;
    readonly Uri url;
    public DohProvider Provider { get; }
    long ok, failed;
    public long Succeeded => Interlocked.Read(ref ok);
    public long Failed => Interlocked.Read(ref failed);
    /// <summary>The last failure, for diagnostics ("" if none).</summary>
    public volatile string LastError = "";

    public DohUpstream(DohProvider provider, HttpMessageHandler? handler = null)
    {
        Provider = provider;
        url = new Uri(provider.Url);
        http = new HttpClient(handler ?? DefaultHandler(provider), true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
    }

    static HttpMessageHandler DefaultHandler(DohProvider p) => new SocketsHttpHandler
    {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (_, ct) =>
        {
            Exception? last = null;
            foreach (var ip in p.Bootstrap)
            {
                var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await s.ConnectAsync(new IPEndPoint(ip, 443), ct); return new NetworkStream(s, true); }
                catch (Exception e) when (e is SocketException or OperationCanceledException) { s.Dispose(); last = e; if (ct.IsCancellationRequested) throw; }
            }
            throw new HttpRequestException("could not reach " + p.Name, last);
        },
    };

    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct)
    {
        if (!DnsWire.TryParseQuestion(query, out _, out _, out int qEnd)) return null;
        try
        {
            var wire = (byte[])query.Clone();
            wire[0] = wire[1] = 0;                                   // RFC 8484: the ID is carried by HTTP, so use 0 (also keeps requests cache-friendly)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            req.Content = new ByteArrayContent(wire);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return Fail($"HTTP {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength > MaxResponse) return Fail("answer too large");
            using var ms = new MemoryStream();
            await using (var body = await resp.Content.ReadAsStreamAsync(cts.Token))
            {
                var buf = new byte[4096]; int n;
                while ((n = await body.ReadAsync(buf, cts.Token)) > 0)
                {
                    if (ms.Length + n > MaxResponse) return Fail("answer too large");
                    ms.Write(buf, 0, n);
                }
            }
            var r = ms.ToArray();
            // must be a response to this question: right size, "response" bit set, and the same question section echoed back
            if (r.Length < qEnd || (r[2] & 0x80) == 0) return Fail("not a DNS response");
            if (!r.AsSpan(12, qEnd - 12).SequenceEqual(query.AsSpan(12, qEnd - 12))) return Fail("answer is for a different question");
            r[0] = query[0]; r[1] = query[1];
            Interlocked.Increment(ref ok);
            LastError = "";
            return r;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return Fail(e is OperationCanceledException ? "timed out" : e.Message);
        }
    }

    byte[]? Fail(string why) { Interlocked.Increment(ref failed); LastError = why; return null; }

    public void Dispose() => http.Dispose();
}
