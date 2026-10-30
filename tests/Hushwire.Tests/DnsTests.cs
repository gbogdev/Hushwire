using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

public class DnsWireTests
{
    [Fact]
    public void QueryRoundTrips()
    {
        var q = DnsWire.BuildQuery("Ads.Example.COM", 28);
        Assert.True(DnsWire.TryParseQuestion(q, out var name, out var qtype, out var qEnd));
        Assert.Equal("ads.example.com", name);
        Assert.Equal(28, qtype);
        Assert.Equal(q.Length, qEnd);
    }

    [Fact]
    public void BlockAnswersKeepTheQueryId()
    {
        var q = DnsWire.BuildQuery("ads.example.com", 1);
        DnsWire.TryParseQuestion(q, out _, out var qtype, out var qEnd);
        var nx = DnsWire.BuildBlock(q, qEnd, qtype, BlockMode.Nxdomain);
        var nul = DnsWire.BuildBlock(q, qEnd, qtype, BlockMode.NullIp);
        Assert.Equal(q[0], nx[0]); Assert.Equal(q[1], nx[1]);
        Assert.Equal(3, nx[3] & 0x0F);
        Assert.Contains(IPAddress.Any, DnsWire.ExtractAddresses(nul));
        Assert.True((nx[2] & 0x80) != 0 && (nul[2] & 0x80) != 0);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    public void ShortOrEmptyInputIsRejected(byte[] m) => Assert.False(DnsWire.TryParseQuestion(m, out _, out _, out _));

    [Fact]
    public void ResponsesAreNotTreatedAsQueries()
    {
        var q = DnsWire.BuildQuery("example.com", 1);
        q[2] |= 0x80;
        Assert.False(DnsWire.TryParseQuestion(q, out _, out _, out _));
    }

    [Fact]
    public void OverlongNameIsRejected()
    {
        var msg = new List<byte> { 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 6; i++) { msg.Add(60); msg.AddRange(Enumerable.Repeat((byte)'a', 60)); }
        msg.AddRange([0, 0, 1, 0, 1]);
        Assert.False(DnsWire.TryParseQuestion(msg.ToArray(), out _, out _, out _));
    }

    [Fact]
    public void CompressionPointerInAQuestionIsRejected()
    {
        var msg = new byte[] { 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1 };
        Assert.False(DnsWire.TryParseQuestion(msg, out _, out _, out _));
    }

    [Fact]
    public void RandomAndMutatedPacketsNeverThrowOrHang()
    {
        var rnd = new Random(1234);
        var valid = new List<byte[]>
        {
            DnsWire.BuildQuery("example.com", 1),
            DnsWire.BuildAddress(DnsWire.BuildQuery("example.com", 1), 29, 1, IPAddress.Parse("1.2.3.4"), 60),
            DnsWire.BuildBlock(DnsWire.BuildQuery("example.com", 28), 29, 28, BlockMode.NullIp),
        };
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 60_000; i++)
        {
            byte[] m;
            if (i % 2 == 0) { m = new byte[rnd.Next(0, 300)]; rnd.NextBytes(m); }
            else
            {
                m = (byte[])valid[rnd.Next(valid.Count)].Clone();
                for (int k = rnd.Next(1, 6); k > 0; k--) if (m.Length > 0) m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                if (rnd.Next(4) == 0 && m.Length > 1) m = m[..rnd.Next(m.Length)];
            }
            DnsWire.TryParseQuestion(m, out _, out _, out var qEnd);
            DnsWire.ParseEdns(m, Math.Min(Math.Max(qEnd, 0), m.Length));
            DnsWire.Inspect(m);
            DnsWire.ExtractAddresses(m);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "parsing took suspiciously long: " + sw.Elapsed);
    }

    [Fact]
    public void ResponseWithACompressionLoopTerminates()
    {
        var m = new List<byte> { 0, 1, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 1, (byte)'a', 0, 0, 1, 0, 1 };
        int self = m.Count;
        m.AddRange([0xC0, (byte)self, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 1, 2, 3, 4]);
        var sw = Stopwatch.StartNew();
        DnsWire.Inspect(m.ToArray()); DnsWire.ExtractAddresses(m.ToArray());
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }
}

public class DnsCacheTests
{
    static (byte[] resp, ResponseInfo info) Answer(uint ttl)
    {
        var q = DnsWire.BuildQuery("example.com", 1);
        var r = DnsWire.BuildAddress(q, q.Length, 1, IPAddress.Parse("1.2.3.4"), ttl);
        return (r, DnsWire.Inspect(r)!);
    }

    [Fact]
    public void HitsExpireAndCarryTheCallersId()
    {
        double now = 100;
        var cache = new DnsCache(() => now);
        var (resp, info) = Answer(60);
        var key = new CacheKey("example.com", 1, 0);
        cache.Put(key, resp, info);
        Assert.True(cache.TryGet(key, 0xBEEF, out var hit));
        Assert.Equal(0xBE, hit[0]); Assert.Equal(0xEF, hit[1]);
        now += 30;
        Assert.True(cache.TryGet(key, 1, out var older));
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(older.AsSpan(older.Length - 10)) is > 0 and <= 60);
        now += 31;
        Assert.False(cache.TryGet(key, 1, out _));
    }

    [Fact]
    public void ServerFailuresAreNotCached()
    {
        var cache = new DnsCache();
        var q = DnsWire.BuildQuery("example.com", 1);
        var fail = DnsWire.BuildServFail(q, q.Length);
        cache.Put(new CacheKey("example.com", 1, 0), fail, DnsWire.Inspect(fail)!);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DnssecAndPlainAnswersDoNotShareAnEntry()
    {
        var cache = new DnsCache();
        var (resp, info) = Answer(60);
        cache.Put(new CacheKey("example.com", 1, 0), resp, info);
        Assert.False(cache.TryGet(new CacheKey("example.com", 1, 3), 1, out _));
    }
}

public class RuleParserTests
{
    static RuleKind P(string line, out List<ParsedRule> rules) { rules = []; return RuleParser.Parse(line, rules); }

    [Fact] public void HostsLineBlocksTheExactName() { Assert.Equal(RuleKind.Block, P("0.0.0.0 ads.example.com", out var r)); Assert.Equal(new ParsedRule("ads.example.com", false, false), r[0]); }
    [Fact] public void AdblockDomainRuleBlocksSubdomainsToo() { Assert.Equal(RuleKind.Block, P("||ads.example.com^", out var r)); Assert.True(r[0].Suffix); }
    [Fact] public void ExceptionRuleAllows() { Assert.Equal(RuleKind.Allow, P("@@||good.example.com^", out var r)); Assert.True(r[0].Allow); }

    [Theory]
    [InlineData("# comment")]
    [InlineData("! comment")]
    [InlineData("")]
    [InlineData("   ")]
    public void CommentsAndBlanksAreSkipped(string line) => Assert.Equal(RuleKind.Skip, P(line, out _));

    [Theory]
    [InlineData("example.com##.banner")]
    [InlineData("||example.com/path/ads.js")]
    public void CosmeticAndPathRulesAreNeverTurnedIntoBlocks(string line)
    {
        Assert.NotEqual(RuleKind.Block, P(line, out var r));
        Assert.Empty(r);
    }

    [Fact]
    public void HugeAndOddLinesDoNotThrow()
    {
        P(new string('a', 100_000), out _);
        P("0.0.0.0 " + new string('\u0000', 50), out _);
        P("||" + new string('é', 300) + "^", out _);
    }
}

public class FilterDecisionTests
{
    static FilterSnapshot Build(string[] listLines, string[]? allow = null, string[]? block = null)
    {
        var b = new FilterBuilder();
        b.AddList("l1", listLines);
        return b.Build(allow ?? [], block ?? []);
    }

    [Fact]
    public void SubdomainsOfASuffixRuleAreBlockedButSiblingsAreNot()
    {
        var f = Build(["||ads.example.com^"]);
        Assert.True(f.Decide("ads.example.com").Blocked);
        Assert.True(f.Decide("x.y.ads.example.com").Blocked);
        Assert.False(f.Decide("example.com").Blocked);
        Assert.False(f.Decide("notads.example.com").Blocked);
        Assert.False(f.Decide("ads.example.com.evil.net").Blocked);
    }

    [Fact]
    public void HostsEntriesMatchOnlyTheExactName()
    {
        var f = Build(["0.0.0.0 ads.example.com"]);
        Assert.True(f.Decide("ads.example.com").Blocked);
        Assert.False(f.Decide("x.ads.example.com").Blocked);
    }

    [Fact]
    public void MoreSpecificUserRuleWins()
    {
        var f = Build(["||tracker.example.com^"], allow: ["tracker.example.com"]);
        Assert.Equal(Origin.UserAllow, f.Decide("tracker.example.com").Origin);
        var g = Build([], block: ["example.com"], allow: ["ok.example.com"]);
        Assert.True(g.Decide("a.example.com").Blocked);
        Assert.False(g.Decide("ok.example.com").Blocked);
        Assert.False(g.Decide("x.ok.example.com").Blocked);
    }

    [Fact]
    public void ListExceptionBeatsListBlock()
    {
        var f = Build(["||example.com^", "@@||good.example.com^"]);
        Assert.True(f.Decide("bad.example.com").Blocked);
        Assert.False(f.Decide("good.example.com").Blocked);
    }

    [Theory]
    [InlineData("windowsupdate.com")]
    [InlineData("download.windowsupdate.com")]
    [InlineData("login.live.com")]
    [InlineData("time.windows.com")]
    [InlineData("steampowered.com")]
    [InlineData("store.steampowered.com")]
    [InlineData("auth.riotgames.com")]
    [InlineData("epicgames.com")]
    [InlineData("battle.net")]
    [InlineData("github.com")]
    [InlineData("raw.githubusercontent.com")]
    public void SystemAndGamePlatformDomainsCannotBeBlockedByLists(string host)
    {
        var f = Build(["||windowsupdate.com^", "||login.live.com^", "||time.windows.com^", "||steampowered.com^", "||riotgames.com^",
            "||epicgames.com^", "||battle.net^", "||github.com^", "||raw.githubusercontent.com^"]);
        Assert.False(f.Decide(host).Blocked);
    }

    [Fact]
    public void EmptyAndOddNamesDoNotThrow()
    {
        var f = Build(["||example.com^"]);
        Assert.False(f.Decide("").Blocked);
        Assert.False(f.Decide(".").Blocked);
        Assert.False(f.Decide(new string('a', 400)).Blocked);
        Assert.False(f.Decide(string.Join('.', Enumerable.Repeat("a", 200))).Blocked);
    }

    [Fact]
    public void ChangingUserRulesKeepsTheLists()
    {
        var f = Build(["||example.com^"]);
        var g = f.WithUserRules(["example.com"], []);
        Assert.False(g.Decide("example.com").Blocked);
        Assert.Equal(f.RuleCount, g.RuleCount);
    }
}
