using System.Buffers.Binary;
using System.Text;

namespace Hushwire.Core;

public enum BlockMode { NullIp, Nxdomain }

public sealed class ResponseInfo
{
    public int Rcode;
    public bool Truncated;
    /// <summary>Min TTL over answer+authority records; null when there are none.</summary>
    public uint? MinTtl;
    public int[] TtlOffsets = [];
    public uint[] Ttls = [];
    public List<string> Cnames = [];
}

/// <summary>Minimal DNS wire helpers. Everything is bounds-checked; malformed input yields false/null, never throws.</summary>
public static class DnsWire
{
    public static bool TryParseQuestion(ReadOnlySpan<byte> m, out string name, out ushort qtype, out int qEnd)
    {
        name = ""; qtype = 0; qEnd = 0;
        if (m.Length < 17 || (m[2] & 0x80) != 0) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(m[4..]) != 1) return false;
        int p = 12, n = 0;
        Span<char> chars = stackalloc char[256];   // one string is created at the end, not a builder plus copies per query
        while (true)
        {
            if (p >= m.Length) return false;
            int len = m[p++];
            if (len == 0) break;
            if ((len & 0xC0) != 0 || p + len > m.Length || n + len + 1 > 253) return false;
            if (n > 0) chars[n++] = '.';
            for (int i = 0; i < len; i++) { byte b = m[p + i]; chars[n++] = (char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b); }
            p += len;
        }
        if (p + 4 > m.Length) return false;
        qtype = BinaryPrimitives.ReadUInt16BigEndian(m[p..]);
        qEnd = p + 4;
        name = new string(chars[..n]);
        return true;
    }

    /// <summary>Buffer size and DNSSEC-OK flag from the query's OPT record, or null if the query has no EDNS.</summary>
    public static (ushort Payload, bool Do)? ParseEdns(ReadOnlySpan<byte> q, int qEnd)
    {
        if (q.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(q[10..]) == 0) return null;
        int p = qEnd; // OPT is normally the first (only) additional record: root name, type 41
        if (p + 11 > q.Length || q[p] != 0 || BinaryPrimitives.ReadUInt16BigEndian(q[(p + 1)..]) != 41) return null;
        return (BinaryPrimitives.ReadUInt16BigEndian(q[(p + 3)..]), (q[p + 7] & 0x80) != 0);
    }

    /// <summary>A recursive query for <paramref name="name"/> (class IN) with a random id.</summary>
    public static byte[] BuildQuery(string name, ushort qtype)
    {
        var m = new List<byte>(32);
        var id = new byte[2]; Random.Shared.NextBytes(id);
        m.AddRange(id); m.AddRange(new byte[] { 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            if (label.Length is 0 or > 63) throw new ArgumentException("invalid domain name", nameof(name));
            m.Add((byte)label.Length); m.AddRange(Encoding.ASCII.GetBytes(label));
        }
        m.Add(0); m.Add((byte)(qtype >> 8)); m.Add((byte)qtype); m.Add(0); m.Add(1);
        return m.ToArray();
    }

    /// <summary>All A / AAAA addresses in the answer section.</summary>
    public static List<System.Net.IPAddress> ExtractAddresses(ReadOnlySpan<byte> m)
    {
        var result = new List<System.Net.IPAddress>();
        if (m.Length < 12 || (m[3] & 0x0F) != 0) return result;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(m[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        int p = 12;
        for (int i = 0; i < qd; i++) { if (!SkipName(m, ref p)) return result; p += 4; }
        for (int i = 0; i < an; i++)
        {
            if (!SkipName(m, ref p) || p + 10 > m.Length) return result;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[p..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(m[(p + 8)..]);
            int rd = p + 10;
            if (rd + rdLen > m.Length) return result;
            if ((type == 1 && rdLen == 4) || (type == 28 && rdLen == 16)) result.Add(new System.Net.IPAddress(m.Slice(rd, rdLen)));
            p = rd + rdLen;
        }
        return result;
    }

    /// <summary>A response answering an A or AAAA question with <paramref name="ip"/>.</summary>
    public static byte[] BuildAddress(byte[] q, int qEnd, ushort qtype, System.Net.IPAddress ip, uint ttl)
    {
        var addr = ip.GetAddressBytes();
        var r = new byte[qEnd + 12 + addr.Length];
        Buffer.BlockCopy(q, 0, r, 0, qEnd);
        r[2] = (byte)(0x80 | (q[2] & 0x01)); r[3] = 0x80;
        r[6] = 0; r[7] = 1; r[8] = r[9] = r[10] = r[11] = 0;
        int p = qEnd;
        r[p++] = 0xC0; r[p++] = 0x0C;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), qtype); p += 2;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), 1); p += 2;
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(p), ttl); p += 4;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), (ushort)addr.Length); p += 2;
        Buffer.BlockCopy(addr, 0, r, p, addr.Length);
        return r;
    }

    /// <summary>NOERROR with an empty answer: "this name exists but has no records of that type".</summary>
    public static byte[] BuildNoData(byte[] q, int qEnd)
    {
        var r = new byte[qEnd];
        Buffer.BlockCopy(q, 0, r, 0, qEnd);
        r[2] = (byte)(0x80 | (q[2] & 0x01)); r[3] = 0x80;
        r[6] = r[7] = r[8] = r[9] = r[10] = r[11] = 0;
        return r;
    }

    public static byte[] BuildBlock(byte[] q, int qEnd, ushort qtype, BlockMode mode)
    {
        bool addr = mode == BlockMode.NullIp && qtype is 1 or 28;
        int rdLen = qtype == 1 ? 4 : 16;
        var r = new byte[qEnd + (addr ? 12 + rdLen : 0)];
        Buffer.BlockCopy(q, 0, r, 0, qEnd);
        r[2] = (byte)(0x80 | (q[2] & 0x01));                  // QR, echo RD
        r[3] = (byte)(0x80 | (mode == BlockMode.Nxdomain ? 3 : 0)); // RA, rcode
        r[6] = 0; r[7] = (byte)(addr ? 1 : 0);
        r[8] = r[9] = r[10] = r[11] = 0;
        if (addr)
        {
            int p = qEnd;
            r[p++] = 0xC0; r[p++] = 0x0C;                      // name → question
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), qtype); p += 2;
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), 1); p += 2;
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(p), 10); p += 4;   // TTL 10 s
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), (ushort)rdLen);
            // rdata stays zero: 0.0.0.0 / ::
        }
        return r;
    }

    public static byte[] BuildServFail(byte[] q, int qEnd)
    {
        var r = new byte[qEnd];
        Buffer.BlockCopy(q, 0, r, 0, qEnd);
        r[2] = (byte)(0x80 | (q[2] & 0x01));
        r[3] = 0x82;
        r[6] = r[7] = r[8] = r[9] = r[10] = r[11] = 0;
        return r;
    }

    static bool SkipName(ReadOnlySpan<byte> m, ref int p)
    {
        while (true)
        {
            if (p >= m.Length) return false;
            int len = m[p];
            if (len == 0) { p++; return true; }
            if ((len & 0xC0) == 0xC0) { p += 2; return p <= m.Length; }
            if ((len & 0xC0) != 0) return false;
            p += 1 + len;
        }
    }

    static string? ReadName(ReadOnlySpan<byte> m, int p)
    {
        var sb = new StringBuilder();
        for (int hops = 0; hops < 20;)
        {
            if (p >= m.Length) return null;
            int len = m[p];
            if (len == 0) return sb.ToString();
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= m.Length) return null;
                p = ((len & 0x3F) << 8) | m[p + 1];
                hops++;
                continue;
            }
            if ((len & 0xC0) != 0 || p + 1 + len > m.Length || sb.Length + len > 253) return null;
            if (sb.Length > 0) sb.Append('.');
            for (int i = 1; i <= len; i++) { byte b = m[p + i]; sb.Append((char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b)); }
            p += 1 + len;
        }
        return null;
    }

    public static ResponseInfo? Inspect(ReadOnlySpan<byte> m)
    {
        if (m.Length < 12) return null;
        var info = new ResponseInfo { Rcode = m[3] & 0x0F, Truncated = (m[2] & 0x02) != 0 };
        int qd = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(m[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(m[10..]);
        int p = 12;
        for (int i = 0; i < qd; i++) { if (!SkipName(m, ref p)) return null; p += 4; }
        var offs = new List<int>(); var ttls = new List<uint>();
        uint? min = null;
        for (int i = 0; i < an + ns + ar; i++)
        {
            if (!SkipName(m, ref p) || p + 10 > m.Length) return null;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[p..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(m[(p + 4)..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(m[(p + 8)..]);
            int rd = p + 10;
            if (rd + rdLen > m.Length) return null;
            if (type != 41)
            {
                offs.Add(p + 4); ttls.Add(ttl);
                if (i < an + ns) min = min is null ? ttl : Math.Min(min.Value, ttl);
            }
            if (type == 5 && i < an) { var t = ReadName(m, rd); if (t is not null) info.Cnames.Add(t); }
            p = rd + rdLen;
        }
        info.MinTtl = min; info.TtlOffsets = offs.ToArray(); info.Ttls = ttls.ToArray();
        return info;
    }
}

/// <summary>What makes two queries share an answer: name, type, and how the client asked (EDNS buffer size and DNSSEC flag).</summary>
public readonly record struct CacheKey(string Name, ushort QType, uint Edns);

public sealed class DnsCache
{
    sealed record Entry(byte[] Data, double Stored, uint Ttl, int[] Offsets, uint[] Ttls);

    const int Max = 20000;
    readonly Dictionary<CacheKey, Entry> map = new();
    readonly object gate = new();
    readonly Func<double> nowSeconds;

    public DnsCache(Func<double>? nowSeconds = null) => this.nowSeconds = nowSeconds ?? (() => Environment.TickCount64 / 1000.0);

    public int Count { get { lock (gate) return map.Count; } }
    public void Clear() { lock (gate) map.Clear(); }

    public void Put(CacheKey key, byte[] resp, ResponseInfo info, uint minTtl = 10, uint maxTtl = 3600)
    {
        if (info.Truncated || info.Rcode is not (0 or 3)) return;
        uint ttl = Math.Clamp(info.MinTtl ?? 30, minTtl, maxTtl);
        lock (gate)
        {
            if (map.Count >= Max) EvictLocked();
            map[key] = new(resp, nowSeconds(), ttl, info.TtlOffsets, info.Ttls);
        }
    }

    void EvictLocked()
    {
        double now = nowSeconds();
        foreach (var k in map.Where(kv => now - kv.Value.Stored >= kv.Value.Ttl).Select(kv => kv.Key).ToList()) map.Remove(k);
        if (map.Count >= Max) foreach (var k in map.Keys.Take(Max / 4).ToList()) map.Remove(k);
    }

    public bool TryGet(CacheKey key, ushort id, out byte[] resp)
    {
        resp = [];
        Entry? e;
        lock (gate) map.TryGetValue(key, out e);
        if (e is null) return false;
        double elapsed = nowSeconds() - e.Stored;
        if (elapsed >= e.Ttl) { lock (gate) map.Remove(key); return false; }
        resp = (byte[])e.Data.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(resp, id);
        uint el = (uint)elapsed;
        for (int i = 0; i < e.Offsets.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(e.Offsets[i]), e.Ttls[i] > el ? Math.Max(1, e.Ttls[i] - el) : 1);
        return true;
    }
}
