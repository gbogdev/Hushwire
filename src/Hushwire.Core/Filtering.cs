using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace Hushwire.Core;

public enum Verdict { Allow, Block }
public enum Origin { Default, Paused, Protected, AppExempt, UserAllow, UserBlock, ListAllow, ListBlock, Cname, Redirect }

public readonly record struct Decision(Verdict Verdict, Origin Origin, string? ListId = null, string? Rule = null)
{
    public bool Blocked => Verdict == Verdict.Block;
}

internal enum NormResult { Ok, Invalid, NonAscii }

public static class Domain
{
    static readonly IdnMapping Idn = new();

    /// <summary>Lowercase ASCII/punycode hostname with at least one dot, or null if invalid.</summary>
    public static string? Normalize(string? s)
    {
        if (s is null) return null;
        Span<char> buf = stackalloc char[256];
        switch (NormalizeInto(s, buf, out int len))
        {
            case NormResult.Ok:
                var done = buf[..len];
                return s.AsSpan().SequenceEqual(done) ? s : new string(done);   // already-clean input is returned as is
            case NormResult.Invalid: return null;
            default: return NormalizeSlow(s);
        }
    }

    /// <summary>
    /// Allocation-free normalization of plain-ASCII names into <paramref name="dest"/> (at least 256 chars).
    /// Names with non-ASCII characters report <see cref="NormResult.NonAscii"/> and need <see cref="NormalizeSlow"/> (IDN/punycode).
    /// </summary>
    internal static NormResult NormalizeInto(ReadOnlySpan<char> s, Span<char> dest, out int len)
    {
        len = 0;
        s = s.Trim();
        if (s.IsEmpty) return NormResult.Invalid;
        s = s.TrimEnd('.');
        if (s.IndexOfAnyExceptInRange('\0', '\u007f') >= 0) return NormResult.NonAscii;
        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        if (s.Length == 0 || s.Length > 253 || s.IndexOf('.') < 0) return NormResult.Invalid;
        int label = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.') { if (label == 0) return NormResult.Invalid; label = 0; dest[i] = '.'; continue; }
            if (c is >= 'A' and <= 'Z') c = (char)(c | 0x20);
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return NormResult.Invalid;
            if (++label > 63) return NormResult.Invalid;
            dest[i] = c;
        }
        if (label == 0) return NormResult.Invalid;
        len = s.Length;
        return NormResult.Ok;
    }

    /// <summary>The general path, including international names. Rare in lists, so it is allowed to allocate.</summary>
    internal static string? NormalizeSlow(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        if (s.Length == 0) return null;
        if (s.Any(c => c > 127))
        {
            try { s = Idn.GetAscii(s); } catch (ArgumentException) { return null; }
        }
        if (s.Length > 253 || s.IndexOf('.') < 0) return null;
        int label = 0;
        foreach (char c in s)
        {
            if (c == '.') { if (label == 0) return null; label = 0; continue; }
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return null;
            if (++label > 63) return null;
        }
        return label == 0 ? null : s;
    }

    /// <summary>name, then each parent: a.b.c → a.b.c, b.c, c.</summary>
    public static IEnumerable<string> Suffixes(string name)
    {
        yield return name;
        int i = 0;
        while ((i = name.IndexOf('.', i)) >= 0) { i++; if (i < name.Length) yield return name[i..]; }
    }
}

public enum RuleKind { Skip, Invalid, Unsupported, Block, Allow }

public readonly record struct ParsedRule(string Domain, bool Suffix, bool Allow);

internal interface IRuleSink
{
    /// <summary>A normalized (lowercase ASCII) domain extracted from a line.</summary>
    void Add(ReadOnlySpan<char> domain, bool suffix, bool allow);
}

internal struct ListSink(List<ParsedRule> list) : IRuleSink
{
    public void Add(ReadOnlySpan<char> domain, bool suffix, bool allow) => list.Add(new(new string(domain), suffix, allow));
}

internal struct NullSink : IRuleSink
{
    public void Add(ReadOnlySpan<char> domain, bool suffix, bool allow) { }
}

/// <summary>Parses the DNS-relevant subset of hosts / domain-list / Adblock-style syntax. Data only, never executed.</summary>
public static class RuleParser
{
    static readonly HashSet<string> HostsIgnore = new(StringComparer.Ordinal)
    {
        "localhost.localdomain", "local", "broadcasthost", "ip6-localhost", "ip6-loopback",
        "ip6-localnet", "ip6-mcastprefix", "ip6-allnodes", "ip6-allrouters", "ip6-allhosts",
    };
    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> HostsIgnoreLookup = HostsIgnore.GetAlternateLookup<ReadOnlySpan<char>>();

    static readonly System.Buffers.SearchValues<char> DomainRuleStops = System.Buffers.SearchValues.Create("/^*:|?=");
    static readonly System.Buffers.SearchValues<char> PlainRuleStops = System.Buffers.SearchValues.Create("/:|*^$@?=");

    public static RuleKind Parse(string raw, List<ParsedRule> sink)
    {
        var s = new ListSink(sink);
        return ParseCore(raw, ref s);
    }

    internal static RuleKind ParseCore<TSink>(ReadOnlySpan<char> raw, ref TSink sink) where TSink : struct, IRuleSink
    {
        var line = raw.Trim();
        if (line.Length == 0) return RuleKind.Skip;
        if (line.Length > 2048) return RuleKind.Invalid;
        char c = line[0];
        if (c is '!' or '[') return RuleKind.Skip;
        if (c == '#' && !(line.Length > 1 && line[1] is '#' or '@' or '?' or '$' or '%')) return RuleKind.Skip;
        if (line.IndexOfAny('#', '$') >= 0 &&
            (line.IndexOf("##", StringComparison.Ordinal) >= 0 || line.IndexOf("#@#", StringComparison.Ordinal) >= 0 ||
             line.IndexOf("#?#", StringComparison.Ordinal) >= 0 || line.IndexOf("#$#", StringComparison.Ordinal) >= 0 ||
             line.IndexOf("#%#", StringComparison.Ordinal) >= 0 || line.IndexOf("$$", StringComparison.Ordinal) >= 0))
            return RuleKind.Unsupported;

        bool allow = false;
        if (line.StartsWith("@@", StringComparison.Ordinal)) { allow = true; line = line[2..]; }
        if (line.Length > 1 && line[0] == '/' && line[^1] == '/') return RuleKind.Unsupported;

        if (line.StartsWith("||", StringComparison.Ordinal))
        {
            var body = line[2..];
            if (body.Contains('$')) return RuleKind.Unsupported;
            if (body.Length > 0 && body[^1] == '^') body = body[..^1];
            if (body.IndexOfAny(DomainRuleStops) >= 0) return RuleKind.Unsupported;
            if (!AddDomain(body, true, allow, ref sink)) return RuleKind.Invalid;
            return allow ? RuleKind.Allow : RuleKind.Block;
        }
        if (allow) return RuleKind.Unsupported;

        int hash = line.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0) line = line[..hash];

        int pos = 0;
        if (!NextToken(line, ref pos, out var first)) return RuleKind.Skip;

        if (IsIp(first))
        {
            int probe = pos;
            if (!NextToken(line, ref probe, out _)) return RuleKind.Unsupported;   // an address with no names
            int added = 0;
            while (NextToken(line, ref pos, out var t))
            {
                if (t[0] == '#') break;
                if (HostsIgnoreLookup.Contains(t) || IsIp(t)) continue;
                if (AddDomain(t, false, false, ref sink)) added++;
            }
            return added > 0 ? RuleKind.Block : RuleKind.Skip;
        }

        int p2 = pos;
        if (NextToken(line, ref p2, out _)) return RuleKind.Invalid;               // several words that aren't a hosts line
        if (line.IndexOfAny(PlainRuleStops) >= 0) return RuleKind.Unsupported;
        return AddDomain(line, true, false, ref sink) ? RuleKind.Block : RuleKind.Invalid;
    }

    static bool NextToken(ReadOnlySpan<char> s, ref int pos, out ReadOnlySpan<char> token)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        if (pos >= s.Length) { token = default; return false; }
        int start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos])) pos++;
        token = s[start..pos];
        return true;
    }

    /// <summary>Same answer as IPAddress.TryParse, but skips the call for words that cannot be an address.</summary>
    static bool IsIp(ReadOnlySpan<char> t)
    {
        if (t.SequenceEqual("0.0.0.0") || t.SequenceEqual("127.0.0.1")) return true;
        if (!(char.IsAsciiDigit(t[0]) || t[0] == '[' || t.Contains(':'))) return false;
        return IPAddress.TryParse(t, out _);
    }

    static bool AddDomain<TSink>(ReadOnlySpan<char> s, bool suffix, bool allow, ref TSink sink) where TSink : struct, IRuleSink
    {
        Span<char> buf = stackalloc char[256];
        switch (Domain.NormalizeInto(s, buf, out int len))
        {
            case NormResult.Ok: sink.Add(buf[..len], suffix, allow); return true;
            case NormResult.Invalid: return false;
            default:
                var n = Domain.NormalizeSlow(s.ToString());
                if (n is null) return false;
                sink.Add(n, suffix, allow);
                return true;
        }
    }
}

public sealed record ParseStats(int Valid, int Unsupported, int Invalid, int Added);

/// <summary>
/// 64-bit hash of a name computed from its last character backwards, so every parent domain's hash falls out of one
/// pass over the name and no substrings are needed. Rules are stored by hash only (see <see cref="DomainSet"/>).
/// </summary>
internal static class NameHash
{
    public const ulong Offset = 0xcbf29ce484222325UL;
    const ulong Prime = 0x100000001b3UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Step(ulong h, char c) => (h ^ c) * Prime;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Finish(ulong h)
    {
        h ^= h >> 32; h *= 0x9E3779B97F4A7C15UL; h ^= h >> 29;
        return h == 0 ? 1UL : h;   // 0 marks an empty slot
    }

    public static ulong Of(ReadOnlySpan<char> s)
    {
        ulong h = Offset;
        for (int i = s.Length - 1; i >= 0; i--) h = Step(h, s[i]);
        return Finish(h);
    }
}

/// <summary>
/// Open-addressing set of 64-bit name hashes with a small value (the list a rule came from): about 12–25 bytes per rule
/// instead of ~100 for a string dictionary. A false match needs a 64-bit collision (about 1 in 10^13 per lookup at 500k rules).
/// </summary>
internal sealed class DomainSet
{
    ulong[] keys = null!;
    byte[] vals = null!;
    int count, mask, growAt;

    public DomainSet(int capacity = 0)
    {
        int cap = 16;
        while (cap * 0.6 < capacity) cap <<= 1;
        Alloc(cap);
    }

    void Alloc(int cap) { keys = new ulong[cap]; vals = new byte[cap]; mask = cap - 1; growAt = (int)(cap * 0.6); }

    public int Count => count;

    public bool TryAdd(ulong h, byte v)
    {
        if (count >= growAt) Grow();
        int i = (int)h & mask;
        while (true)
        {
            ulong k = keys[i];
            if (k == 0) { keys[i] = h; vals[i] = v; count++; return true; }
            if (k == h) return false;
            i = (i + 1) & mask;
        }
    }

    public bool TryGet(ulong h, out byte v)
    {
        int i = (int)h & mask;
        while (true)
        {
            ulong k = keys[i];
            if (k == h) { v = vals[i]; return true; }
            if (k == 0) { v = 0; return false; }
            i = (i + 1) & mask;
        }
    }

    public bool Contains(ulong h) => TryGet(h, out _);

    void Grow()
    {
        var ok = keys; var ov = vals;
        Alloc(ok.Length * 2);
        for (int j = 0; j < ok.Length; j++)
        {
            ulong k = ok[j];
            if (k == 0) continue;
            int i = (int)k & mask;
            while (keys[i] != 0) i = (i + 1) & mask;
            keys[i] = k; vals[i] = ov[j];
        }
    }
}

/// <summary>Immutable once built; swap atomically.</summary>
public sealed class FilterSnapshot
{
    public static readonly FilterSnapshot Empty = new(new(), new(), new(), new(), [], new(), new());

    readonly DomainSet exactBlock, suffixBlock, exactAllow, suffixAllow;
    readonly string[] listIds;
    readonly DomainSet userAllow, userBlock;

    internal FilterSnapshot(DomainSet eb, DomainSet sb, DomainSet ea, DomainSet sa, string[] ids, DomainSet ua, DomainSet ub)
    {
        exactBlock = eb; suffixBlock = sb; exactAllow = ea; suffixAllow = sa; listIds = ids; userAllow = ua; userBlock = ub;
    }

    public int RuleCount => exactBlock.Count + suffixBlock.Count;
    public int UserRuleCount => userAllow.Count + userBlock.Count;

    static readonly DomainSet Protected = MakeProtected();

    static DomainSet MakeProtected()
    {
        var set = new DomainSet(64);
        foreach (var d in new[]
        {
            "msftconnecttest.com", "msftncsi.com", "windowsupdate.com", "update.microsoft.com",
            "delivery.mp.microsoft.com", "smartscreen.microsoft.com", "smartscreen-prod.microsoft.com",
            "wdcp.microsoft.com", "wdcpalt.microsoft.com", "definitionupdates.microsoft.com",
            "login.live.com", "login.microsoftonline.com", "time.windows.com",
            "raw.githubusercontent.com", "github.com",
            // game launchers, patchers, matchmaking and anti-cheat: a blocked lookup here breaks login or kicks you from a match
            "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com", "steamserver.net", "steamgames.com",
            "steamusercontent.com", "valvesoftware.com", "steamcdn-a.akamaihd.net",
            "riotgames.com", "riotcdn.net", "leagueoflegends.com", "playvalorant.com", "pvp.net",
            "epicgames.com", "unrealengine.com", "easyanticheat.net", "epicgames-download1.akamaized.net",
            "battle.net", "blizzard.com", "battleye.com",
            "ea.com", "origin.com", "ubisoft.com", "ubi.com", "rockstargames.com",
            "xboxlive.com", "xbox.com", "gog.com", "faceit.com",
        }) set.TryAdd(NameHash.Of(d), 1);
        return set;
    }

    internal static DomainSet UserSet(IEnumerable<string> domains)
    {
        var set = new DomainSet();
        foreach (var d in domains) { var n = Domain.Normalize(d); if (n is not null) set.TryAdd(NameHash.Of(n), 1); }
        return set;
    }

    /// <summary>The same lists with different user allow/block rules: instant, nothing is re-parsed.</summary>
    public FilterSnapshot WithUserRules(IEnumerable<string> userAllowRules, IEnumerable<string> userBlockRules)
        => new(exactBlock, suffixBlock, exactAllow, suffixAllow, listIds, UserSet(userAllowRules), UserSet(userBlockRules));

    /// <summary>
    /// 1) user rules, most specific suffix first (allow wins a tie); 2) protected OS domains never blocked by lists;
    /// 3) list exceptions (@@); 4) list blocks. Allocates only when it returns a matching rule.
    /// </summary>
    public Decision Decide(string name)
    {
        if (name.Length == 0) return new(Verdict.Allow, Origin.Default);
        // names from DNS are at most 253 characters (127 labels); anything longer just takes a heap buffer
        if (name.Length <= 255)
        {
            Span<int> starts = stackalloc int[128];
            Span<ulong> hashes = stackalloc ulong[128];
            return DecideCore(name, starts, hashes);
        }
        return DecideCore(name, new int[name.Length + 1], new ulong[name.Length + 1]);
    }

    Decision DecideCore(string name, Span<int> starts, Span<ulong> hashes)
    {
        // one backwards pass: the hash of every suffix that starts at a label boundary (shortest first)
        int k = 0;
        ulong h = NameHash.Offset;
        for (int i = name.Length - 1; i >= 0; i--)
        {
            h = NameHash.Step(h, name[i]);
            if (i == 0 || name[i - 1] == '.') { starts[k] = i; hashes[k] = NameHash.Finish(h); k++; }
        }

        if (userAllow.Count + userBlock.Count > 0)
            for (int j = k - 1; j >= 0; j--)
            {
                if (userAllow.Contains(hashes[j])) return new(Verdict.Allow, Origin.UserAllow, "user", name[starts[j]..]);
                if (userBlock.Contains(hashes[j])) return new(Verdict.Block, Origin.UserBlock, "user", name[starts[j]..]);
            }

        for (int j = k - 1; j >= 0; j--)
            if (Protected.Contains(hashes[j])) return new(Verdict.Allow, Origin.Protected, null, name[starts[j]..]);

        if (suffixAllow.Count + exactAllow.Count > 0)
            for (int j = k - 1; j >= 0; j--)
                if (suffixAllow.TryGet(hashes[j], out var a) || (j == k - 1 && exactAllow.TryGet(hashes[j], out a)))
                    return new(Verdict.Allow, Origin.ListAllow, listIds[a], name[starts[j]..]);

        for (int j = k - 1; j >= 0; j--)
            if (suffixBlock.TryGet(hashes[j], out var b) || (j == k - 1 && exactBlock.TryGet(hashes[j], out b)))
                return new(Verdict.Block, Origin.ListBlock, listIds[b], name[starts[j]..]);

        return new(Verdict.Allow, Origin.Default);
    }
}

public sealed class FilterBuilder
{
    readonly DomainSet eb, sb, ea = new(), sa = new();
    readonly List<string> ids = [];

    /// <param name="capacityHint">Rough number of rules to expect; avoids repeatedly growing the tables.</param>
    public FilterBuilder(int capacityHint = 0)
    {
        eb = new(capacityHint / 2);
        sb = new(capacityHint / 2);
    }

    struct HashSink(DomainSet eb, DomainSet sb, DomainSet ea, DomainSet sa, byte idx) : IRuleSink
    {
        public int Valid, Added;
        public void Add(ReadOnlySpan<char> domain, bool suffix, bool allow)
        {
            Valid++;
            var set = allow ? (suffix ? sa : ea) : (suffix ? sb : eb);
            if (set.TryAdd(NameHash.Of(domain), idx)) Added++;
        }
    }

    struct LineHandler(HashSink sink) : ILineHandler
    {
        public HashSink Sink = sink;
        public int Unsupported, Invalid;
        public void Line(ReadOnlySpan<char> line)
        {
            switch (RuleParser.ParseCore(line, ref Sink))
            {
                case RuleKind.Unsupported: Unsupported++; break;
                case RuleKind.Invalid: Invalid++; break;
            }
        }
    }

    byte NextIndex(string id)
    {
        if (ids.Count >= 255) throw new InvalidOperationException("too many lists");
        ids.Add(id);
        return (byte)(ids.Count - 1);
    }

    public ParseStats AddList(string id, IEnumerable<string> lines)
    {
        var h = new LineHandler(new HashSink(eb, sb, ea, sa, NextIndex(id)));
        foreach (var line in lines) h.Line(line);
        return new(h.Sink.Valid, h.Unsupported, h.Invalid, h.Sink.Added);
    }

    /// <summary>Same as <see cref="AddList"/> for a UTF-8 file already in memory, without creating a string per line.</summary>
    public ParseStats AddListUtf8(string id, ReadOnlySpan<byte> data)
    {
        var h = new LineHandler(new HashSink(eb, sb, ea, sa, NextIndex(id)));
        Utf8Lines.ForEach(data, ref h);
        return new(h.Sink.Valid, h.Unsupported, h.Invalid, h.Sink.Added);
    }

    public FilterSnapshot Build(IEnumerable<string> userAllow, IEnumerable<string> userBlock)
        => new(eb, sb, ea, sa, ids.ToArray(), FilterSnapshot.UserSet(userAllow), FilterSnapshot.UserSet(userBlock));
}

internal interface ILineHandler
{
    void Line(ReadOnlySpan<char> line);
}

/// <summary>Splits UTF-8 text into lines (\n, \r or \r\n) and hands each over as characters, reusing one buffer.</summary>
internal static class Utf8Lines
{
    public static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    public static void ForEach<THandler>(ReadOnlySpan<byte> data, ref THandler handler) where THandler : struct, ILineHandler
    {
        if (data.StartsWith(Utf8Lines.Bom)) data = data[3..];                       // byte-order mark
        char[] buf = ArrayPool<char>.Shared.Rent(8192);
        try
        {
            while (!data.IsEmpty)
            {
                int nl = data.IndexOfAny((byte)'\n', (byte)'\r');
                var line = nl < 0 ? data : data[..nl];
                data = nl < 0 ? default : data[(nl + 1)..];
                if (line.IsEmpty) continue;
                int max = Encoding.UTF8.GetMaxCharCount(line.Length);
                char[]? big = max > buf.Length ? ArrayPool<char>.Shared.Rent(max) : null;
                var dst = big ?? buf;
                int n = Encoding.UTF8.GetChars(line, dst);
                handler.Line(dst.AsSpan(0, n));
                if (big is not null) ArrayPool<char>.Shared.Return(big);
            }
        }
        finally { ArrayPool<char>.Shared.Return(buf); }
    }
}
