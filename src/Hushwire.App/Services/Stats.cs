using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hushwire.App;

public sealed class DayStat { public long Queries { get; set; } public long Blocked { get; set; } public long Cached { get; set; } }
public sealed class AppStat { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public long Queries { get; set; } public long Blocked { get; set; } }

public sealed class StatsData
{
    public DateTime Since { get; set; } = DateTime.Now;
    public long Queries { get; set; }
    public long Blocked { get; set; }
    public long Cached { get; set; }
    public long CnameBlocked { get; set; }
    public long UpstreamFailures { get; set; }
    public long RewrittenYouTube { get; set; }
    public long RewrittenTwitch { get; set; }
    public Dictionary<string, DayStat> Days { get; set; } = new();
    public Dictionary<string, long> BlockedDomains { get; set; } = new();
    public Dictionary<string, long> BlockedByList { get; set; } = new();
    public Dictionary<string, long> BlockedByCategory { get; set; } = new();
    public Dictionary<string, AppStat> Apps { get; set; } = new();
}

public sealed record Row(string Label, string Value);

/// <summary>
/// Counts DNS lookups. A "blocked" lookup is a request for a hostname on a blocklist; it is NOT a count of ads,
/// cookies or banners, and one page load can produce many lookups.
/// </summary>
public sealed class Stats
{
    readonly object gate = new();
    StatsData d = new();
    public long SessionQueries, SessionBlocked;

    public static Stats Load()
    {
        var s = new Stats();
        try { s.d = JsonSerializer.Deserialize<StatsData>(File.ReadAllText(Paths.StatsFile)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException)
        {
            if (e is JsonException) SettingsStore.KeepCopy(Paths.StatsFile);
        }
        return s;
    }

    // Today's counters are looked up once per day, not formatted and searched on every query.
    DayStat? today; DateTime todayDate;
    DayStat Today()
    {
        var now = DateTime.Now;
        if (today is null || now.Date != todayDate)
        {
            todayDate = now.Date;
            var key = now.ToString("yyyy-MM-dd");
            if (!d.Days.TryGetValue(key, out today)) d.Days[key] = today = new();
        }
        return today;
    }

    long version, savedVersion;
    /// <summary>Changes whenever any counter does: lets the UI skip redrawing and the saver skip writing.</summary>
    public long Version { get { lock (gate) return version; } }

    public void Record(string domain, bool blocked, bool cached, bool failed, bool cname, string category, string source, AppInfo? app)
    {
        lock (gate)
        {
            version++;
            d.Queries++; SessionQueries++;
            if (cached) d.Cached++;
            if (failed) d.UpstreamFailures++;
            var day = Today();
            day.Queries++; if (cached) day.Cached++;
            if (blocked)
            {
                d.Blocked++; SessionBlocked++; day.Blocked++;
                if (cname) d.CnameBlocked++;
                Bump(d.BlockedDomains, domain);
                if (d.BlockedDomains.Count > 5000) TrimDomains();
                Bump(d.BlockedByList, source);
                Bump(d.BlockedByCategory, category);
            }
            if (app is not null)
            {
                var key = app.Direct ? app.Path : "dnsclient";
                if (!d.Apps.TryGetValue(key, out var a)) d.Apps[key] = a = new() { Name = app.Name, Path = app.Direct ? app.Path : "" };
                a.Queries++; if (blocked) a.Blocked++;
            }
        }
    }

    /// <summary>Keeps memory bounded when thousands of one-off (e.g. randomly generated) domains are blocked.</summary>
    void TrimDomains() => d.BlockedDomains = d.BlockedDomains.OrderByDescending(kv => kv.Value).Take(2000).ToDictionary(kv => kv.Key, kv => kv.Value);

    static void Bump(Dictionary<string, long> m, string k) { m.TryGetValue(k, out var v); m[k] = v + 1; }

    public void RecordRewrite(string site, int n) { lock (gate) { version++; if (site == "twitch") d.RewrittenTwitch += n; else d.RewrittenYouTube += n; } }
    public (long YouTube, long Twitch) Rewritten { get { lock (gate) return (d.RewrittenYouTube, d.RewrittenTwitch); } }

    public (long Queries, long Blocked, long Cached, long Failures, long Cname, DateTime Since) Lifetime
    { get { lock (gate) return (d.Queries, d.Blocked, d.Cached, d.UpstreamFailures, d.CnameBlocked, d.Since); } }

    public DayStat Day() { lock (gate) { var x = Today(); return new() { Queries = x.Queries, Blocked = x.Blocked, Cached = x.Cached }; } }

    static List<Row> Top(Dictionary<string, long> m, int n) =>
        m.OrderByDescending(kv => kv.Value).Take(n).Select(kv => new Row(kv.Key, kv.Value.ToString("N0"))).ToList();

    public List<Row> TopDomains(int n = 12) { lock (gate) return Top(d.BlockedDomains, n); }
    public List<Row> ByList() { lock (gate) return Top(d.BlockedByList, 8); }
    public List<Row> ByCategory() { lock (gate) return Top(d.BlockedByCategory, 8); }
    public List<AppStat> Apps() { lock (gate) return d.Apps.Values.Select(a => new AppStat { Name = a.Name, Path = a.Path, Queries = a.Queries, Blocked = a.Blocked }).OrderByDescending(a => a.Queries).ToList(); }
    public List<Row> TopApps(int n = 8) { lock (gate) return d.Apps.Values.OrderByDescending(a => a.Blocked).ThenByDescending(a => a.Queries).Take(n).Select(a => new Row(a.Name, $"{a.Blocked:N0} / {a.Queries:N0}")).ToList(); }

    public void Reset() { lock (gate) { d = new(); today = null; version++; SessionQueries = SessionBlocked = 0; } Save(); }

    public void Save()
    {
        string json;
        long saving;
        lock (gate)
        {
            if (version == savedVersion) return;           // nothing changed since the last write: don't touch the disk
            saving = version;
            if (d.Days.Count > 90) foreach (var k in d.Days.Keys.OrderBy(x => x).Take(d.Days.Count - 90).ToList()) d.Days.Remove(k);
            json = JsonSerializer.Serialize(d);
        }
        try
        {
            Paths.Ensure();
            var tmp = Paths.StatsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.StatsFile, true);
            lock (gate) savedVersion = saving;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // runs on a timer thread: an escaping exception would end the process
    }
}
