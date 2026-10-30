using System.Net;
using System.Net.Sockets;
using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

/// <summary>Real sockets on loopback: a fake upstream DNS server and the real <see cref="DnsProxy"/> in front of it.</summary>
public sealed class ProxyHarness : IDisposable
{
    readonly UdpClient upstream = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly CancellationTokenSource stop = new();
    public int UpstreamPort => ((IPEndPoint)upstream.Client.LocalEndPoint!).Port;
    public int UpstreamHits;
    public int DropFirst;                          // ignore this many queries before answering
    public Func<byte[], byte[]?>? Responder;       // custom answer; default is 1.2.3.4
    public DnsProxy Proxy = null!;
    public int ProxyPort;
    public bool Active = true;

    public ProxyHarness(FilterSnapshot? snapshot = null, bool startUpstream = true, int[]? attemptsMs = null)
    {
        if (startUpstream) _ = Task.Run(Serve);
        else upstream.Dispose();
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); ProxyPort = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        int up = startUpstream ? UpstreamPort : 9;     // port 9 (discard): nothing listens there
        Proxy = new DnsProxy(ProxyPort)
        {
            Snapshot = () => snapshot ?? FilterSnapshot.Empty,
            Active = () => Active,
            Upstreams = () => [IPAddress.Loopback],
            UpstreamPort = up,
            UdpAttemptsMs = attemptsMs ?? [200, 200, 400],
        };
        Proxy.Start();
    }

    async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await upstream.ReceiveAsync(stop.Token); } catch { return; }
            int n = Interlocked.Increment(ref UpstreamHits);
            if (n <= DropFirst) continue;
            byte[]? resp;
            if (Responder is not null) resp = Responder(r.Buffer);
            else
            {
                DnsWire.TryParseQuestion(r.Buffer, out _, out _, out var qEnd);
                resp = DnsWire.BuildAddress(r.Buffer, qEnd, 1, IPAddress.Parse("1.2.3.4"), 60);
            }
            if (resp is not null) await upstream.SendAsync(resp, resp.Length, r.RemoteEndPoint);
        }
    }

    public byte[]? Ask(string name, ushort type = 1, int timeoutMs = 3000)
    {
        using var c = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        c.Client.ReceiveTimeout = timeoutMs;
        var q = DnsWire.BuildQuery(name, type);
        c.Send(q, q.Length, new IPEndPoint(IPAddress.Loopback, ProxyPort));
        try { var ep = new IPEndPoint(IPAddress.Any, 0); return c.Receive(ref ep); } catch (SocketException) { return null; }
    }

    public byte[]? AskTcp(string name)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, ProxyPort);
        c.ReceiveTimeout = 3000;
        var s = c.GetStream();
        var q = DnsWire.BuildQuery(name, 1);
        s.Write([(byte)(q.Length >> 8), (byte)q.Length]); s.Write(q);
        var len = new byte[2]; s.ReadExactly(len);
        var r = new byte[(len[0] << 8) | len[1]]; s.ReadExactly(r);
        return r;
    }

    public void Dispose() { stop.Cancel(); Proxy.Dispose(); upstream.Dispose(); }
}

public class DnsProxyTests
{
    static FilterSnapshot Blocking(params string[] rules) { var b = new FilterBuilder(); b.AddList("t", rules); return b.Build([], []); }
    static IPAddress First(byte[] r) => DnsWire.ExtractAddresses(r)[0];
    static int Rcode(byte[] r) => r[3] & 0x0F;

    [Fact]
    public void BlockedNamesAreAnsweredLocallyWithoutAskingTheUpstream()
    {
        using var h = new ProxyHarness(Blocking("||ads.example.com^"));
        var r = h.Ask("x.ads.example.com")!;
        Assert.Equal(IPAddress.Any, First(r));
        Assert.Equal(0, h.UpstreamHits);
    }

    [Fact]
    public void AllowedNamesAreForwardedThenServedFromCache()
    {
        using var h = new ProxyHarness(Blocking("||ads.example.com^"));
        Assert.Equal(IPAddress.Parse("1.2.3.4"), First(h.Ask("example.com")!));
        Assert.Equal(IPAddress.Parse("1.2.3.4"), First(h.Ask("example.com")!));
        Assert.Equal(1, h.UpstreamHits);
    }

    [Fact]
    public void ALostUdpPacketIsRetried()
    {
        using var h = new ProxyHarness { DropFirst = 1 };
        var r = h.Ask("example.com");
        Assert.NotNull(r);
        Assert.Equal(0, Rcode(r!));
        Assert.True(h.UpstreamHits >= 2);
    }

    [Fact]
    public void ADeadUpstreamGivesServfailNotSilence()
    {
        using var h = new ProxyHarness(startUpstream: false, attemptsMs: [100, 100]);
        var r = h.Ask("example.com", timeoutMs: 5000);
        Assert.NotNull(r);
        Assert.Equal(2, Rcode(r!));
    }

    [Fact]
    public void PausedProtectionForwardsEvenBlockedNames()
    {
        using var h = new ProxyHarness(Blocking("||ads.example.com^")) { Active = false };
        Assert.Equal(IPAddress.Parse("1.2.3.4"), First(h.Ask("ads.example.com")!));
    }

    [Fact]
    public void TcpQueriesWork()
    {
        using var h = new ProxyHarness(Blocking("||ads.example.com^"));
        Assert.Equal(IPAddress.Any, First(h.AskTcp("ads.example.com")));
    }

    [Fact]
    public void GarbageDatagramsDoNotStopTheProxy()
    {
        using var h = new ProxyHarness();
        using (var c = new UdpClient())
        {
            var rnd = new Random(5);
            for (int i = 0; i < 300; i++) { var b = new byte[rnd.Next(0, 600)]; rnd.NextBytes(b); c.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, h.ProxyPort)); }
        }
        Assert.NotNull(h.Ask("example.com"));
    }

    [Fact]
    public void ManyParallelQueriesAreAllAnswered()
    {
        using var h = new ProxyHarness(Blocking("||ads.example.com^"));
        var results = Enumerable.Range(0, 200).AsParallel().WithDegreeOfParallelism(16)
            .Select(i => h.Ask(i % 2 == 0 ? $"a{i}.ads.example.com" : $"site{i}.example.org")).ToList();
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public void AnAnswerThatAliasesABlockedNameIsBlocked()
    {
        using var h = new ProxyHarness(Blocking("||tracker.example.net^"))
        {
            Responder = q =>
            {
                DnsWire.TryParseQuestion(q, out _, out _, out var qEnd);
                var m = new List<byte>(q[..qEnd]);
                m[2] = 0x81; m[3] = 0x80; m[7] = 1;                    // response, one answer
                m.AddRange([0xC0, 12, 0, 5, 0, 1, 0, 0, 0, 60]);      // CNAME
                var target = new List<byte>();
                foreach (var l in "cdn.tracker.example.net".Split('.')) { target.Add((byte)l.Length); target.AddRange(System.Text.Encoding.ASCII.GetBytes(l)); }
                target.Add(0);
                m.Add(0); m.Add((byte)target.Count); m.AddRange(target);
                return m.ToArray();
            },
        };
        var r = h.Ask("harmless.example.com")!;
        Assert.Equal(IPAddress.Any, First(r));
    }
}

static class HarnessExt
{
    public static T With<T>(this T x, Action<T> a) { a(x); return x; }
}
