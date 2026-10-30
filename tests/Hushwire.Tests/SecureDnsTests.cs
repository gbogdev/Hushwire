using System.Net;
using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

public class SecureDnsMatchTests
{
    [Theory]
    [InlineData("use-application-dns.net", true)]
    [InlineData("dns.google", true)]
    [InlineData("dns64.dns.google", true)]
    [InlineData("cloudflare-dns.com", true)]
    [InlineData("mozilla.cloudflare-dns.com", true)]
    [InlineData("dns.quad9.net", true)]
    [InlineData("notdns.google", false)]
    [InlineData("google.com", false)]
    [InlineData("cloudflare.com", false)]
    [InlineData("example.com", false)]
    [InlineData("dns.google.evil.example", false)]
    [InlineData("", false)]
    public void OnlyKnownSecureDnsNamesMatch(string name, bool expected) => Assert.Equal(expected, SecureDns.Matches(name));
}

/// <summary>Stands in for the DoH server: no network involved.</summary>
sealed class FakeDohHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    public byte[]? LastBody;
    public HttpRequestMessage? LastRequest;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastRequest = request;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        LastBody = body;
        return respond(request, body);
    }

    public static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    /// <summary>A normal server: answers 1.2.3.4 for whatever was asked.</summary>
    public static FakeDohHandler Working() => new((req, body) =>
    {
        DnsWire.TryParseQuestion(body, out _, out _, out var qEnd);
        return Ok(DnsWire.BuildAddress(body, qEnd, 1, IPAddress.Parse("1.2.3.4"), 60));
    });
}

public class DohUpstreamTests
{
    static DohUpstream Make(FakeDohHandler h) => new(DohUpstream.Providers[0], h);

    [Fact]
    public async Task SendsAnRfc8484PostAndPutsTheCallersIdBack()
    {
        var h = FakeDohHandler.Working();
        using var doh = Make(h);
        var q = DnsWire.BuildQuery("example.com", 1);
        q[0] = 0xAB; q[1] = 0xCD;
        var r = await doh.QueryAsync(q, default);
        Assert.NotNull(r);
        Assert.Equal(0xAB, r![0]); Assert.Equal(0xCD, r[1]);
        Assert.Equal(HttpMethod.Post, h.LastRequest!.Method);
        Assert.Equal("application/dns-message", h.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(0, h.LastBody![0]); Assert.Equal(0, h.LastBody[1]);      // ID 0 on the wire
        Assert.Equal("https", h.LastRequest.RequestUri!.Scheme);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), DnsWire.ExtractAddresses(r)[0]);
        Assert.Equal(1, doh.Succeeded);
    }

    [Fact]
    public async Task ServerErrorsAreFailuresNotAnswers()
    {
        using var doh = Make(new FakeDohHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        Assert.Null(await doh.QueryAsync(DnsWire.BuildQuery("example.com", 1), default));
        Assert.Equal(1, doh.Failed);
        Assert.Contains("502", doh.LastError);
    }

    [Fact]
    public async Task AnAnswerForADifferentQuestionIsRejected()
    {
        var other = DnsWire.BuildQuery("other.example.org", 1);
        DnsWire.TryParseQuestion(other, out _, out _, out var qEnd);
        using var doh = Make(new FakeDohHandler((_, _) => FakeDohHandler.Ok(DnsWire.BuildAddress(other, qEnd, 1, IPAddress.Parse("6.6.6.6"), 60))));
        Assert.Null(await doh.QueryAsync(DnsWire.BuildQuery("example.com", 1), default));
    }

    [Fact]
    public async Task ARequestEchoedBackIsNotAcceptedAsAResponse()
    {
        using var doh = Make(new FakeDohHandler((_, body) => FakeDohHandler.Ok(body)));      // QR bit not set
        Assert.Null(await doh.QueryAsync(DnsWire.BuildQuery("example.com", 1), default));
    }

    [Fact]
    public async Task OversizedAndGarbageAnswersAreRejected()
    {
        using var big = Make(new FakeDohHandler((_, _) => FakeDohHandler.Ok(new byte[70_000])));
        Assert.Null(await big.QueryAsync(DnsWire.BuildQuery("example.com", 1), default));
        using var junk = Make(new FakeDohHandler((_, _) => FakeDohHandler.Ok([1, 2, 3])));
        Assert.Null(await junk.QueryAsync(DnsWire.BuildQuery("example.com", 1), default));
    }

    [Fact]
    public async Task NetworkErrorsAreFailuresNotExceptions()
    {
        using var doh = Make(new FakeDohHandler((_, _) => throw new HttpRequestException("no route")));
        Assert.Null(await doh.QueryAsync(DnsWire.BuildQuery("example.com", 1), default));
        Assert.Equal("no route", doh.LastError);
    }

    [Fact]
    public async Task UnparseableQueriesAreNotSent()
    {
        var h = FakeDohHandler.Working();
        using var doh = Make(h);
        Assert.Null(await doh.QueryAsync([1, 2, 3], default));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public void BuiltInProvidersUseHttpsAndHaveAddresses()
    {
        Assert.All(DohUpstream.Providers, p => { Assert.StartsWith("https://", p.Url); Assert.NotEmpty(p.Bootstrap); });
        Assert.Equal("cloudflare", DohUpstream.Find("cloudflare")!.Id);
        Assert.Null(DohUpstream.Find("nope"));
        Assert.Null(DohUpstream.Find(null));
    }
}

public class SecureDnsProxyTests
{
    static int Rcode(byte[] r) => r[3] & 0x0F;

    [Fact]
    public void WhenOnTheCanaryAndDohHostsAreAnsweredAsNonexistent()
    {
        using var h = new ProxyHarness(secureDns: true);
        Assert.Equal(3, Rcode(h.Ask("use-application-dns.net")!));
        Assert.Equal(3, Rcode(h.Ask("dns.google")!));
        Assert.Equal(0, Rcode(h.Ask("example.com")!));
        Assert.Equal(1, h.UpstreamHits);                     // only example.com went upstream
    }

    [Fact]
    public void WhenOffTheyAreResolvedNormally()
    {
        using var h = new ProxyHarness();
        Assert.Equal(0, Rcode(h.Ask("use-application-dns.net")!));
        Assert.Equal(1, h.UpstreamHits);
    }

    [Fact]
    public void AUserAllowRuleOverridesIt()
    {
        using var h = new ProxyHarness(new FilterBuilder().Build(["dns.google"], []), secureDns: true);
        Assert.Equal(0, Rcode(h.Ask("dns.google")!));
    }

    [Fact]
    public void LookupsGoThroughTheEncryptedUpstreamWhenItWorks()
    {
        var doh = FakeDohHandler.Working();
        using var h = new ProxyHarness();
        h.Proxy.Doh = new DohUpstream(DohUpstream.Providers[0], doh);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), DnsWire.ExtractAddresses(h.Ask("example.com")!)[0]);
        Assert.Equal(1, doh.Calls);
        Assert.Equal(0, h.UpstreamHits);                     // nothing in plain text
    }

    [Fact]
    public void AFailingEncryptedUpstreamFallsBackToOrdinaryDnsSoNothingBreaks()
    {
        var doh = new FakeDohHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var h = new ProxyHarness();
        h.Proxy.Doh = new DohUpstream(DohUpstream.Providers[0], doh);
        var r = h.Ask("example.com");
        Assert.NotNull(r);
        Assert.Equal(0, Rcode(r!));
        Assert.Equal(1, h.UpstreamHits);
        Assert.Equal(1, h.Proxy.DohFallbacks);
    }
}
