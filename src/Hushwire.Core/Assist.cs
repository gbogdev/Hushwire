namespace Hushwire.Core;

// ---------------------------------------------------------------------------------------------------------------
// "Something broke": which blocked lookups most likely caused it, and a temporary allow that undoes itself.
// ---------------------------------------------------------------------------------------------------------------

public sealed record BlockHit(string Domain, string App, DateTime Time);
public sealed record Suspect(string Domain, int Hits, string App, DateTime LastSeen, double Score, string Reason);

public static class BreakageRanker
{
    /// <summary>
    /// Blocked lookups from the last few minutes, most likely culprit first. Recent beats old, a name asked for by the program
    /// you are using right now beats background noise, and a name asked for many times (a telemetry loop) is less likely to be
    /// the thing you can see broken.
    /// </summary>
    public static List<Suspect> Rank(IEnumerable<BlockHit> hits, DateTime now, string? foregroundApp, TimeSpan? window = null, int max = 8)
    {
        var span = window ?? TimeSpan.FromMinutes(10);
        var result = new List<Suspect>();
        foreach (var g in hits.Where(h => now - h.Time <= span && h.Time <= now.AddSeconds(5)).GroupBy(h => h.Domain, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            var last = list.MaxBy(h => h.Time)!;
            double ageMin = Math.Max(0, (now - last.Time).TotalMinutes);
            double recency = Math.Max(0, 1 - ageMin / span.TotalMinutes) * 3;
            double freq = list.Count <= 3 ? list.Count * 0.5 : list.Count <= 20 ? 1.5 : -1.0;       // a few repeats are a good sign; dozens are a background loop
            bool front = foregroundApp is { Length: > 0 } && list.Any(h => h.App.Equals(foregroundApp, StringComparison.OrdinalIgnoreCase));
            double score = recency + freq + (front ? 4 : 0);
            string who = front ? foregroundApp! : last.App;
            string when = ageMin < 1 ? "just now" : $"{(int)Math.Round(ageMin)} min ago";
            result.Add(new Suspect(g.Key, list.Count, who, last.Time, score,
                $"Blocked {list.Count}× (last {when}) for {who}{(front ? ", the program in front" : "")}"));
        }
        return result.OrderByDescending(s => s.Score).ThenByDescending(s => s.LastSeen).ThenBy(s => s.Domain, StringComparer.Ordinal).Take(max).ToList();
    }
}

public sealed class TimedAllow
{
    public string Domain { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}

public static class TimedAllows
{
    public static List<string> Active(IEnumerable<TimedAllow> items, DateTime nowUtc) =>
        items.Where(a => a.ExpiresUtc > nowUtc).Select(a => a.Domain).Distinct().ToList();

    /// <summary>Removes the expired ones. True if anything was removed (the rules then need rebuilding).</summary>
    public static bool Prune(List<TimedAllow> items, DateTime nowUtc) => items.RemoveAll(a => a.ExpiresUtc <= nowUtc || Domain.Normalize(a.Domain) is null) > 0;

    /// <summary>Adds or extends an allow. Invalid names are ignored. Returns the normalized domain, or null.</summary>
    public static string? Add(List<TimedAllow> items, string domain, DateTime expiresUtc)
    {
        var d = Domain.Normalize(domain);
        if (d is null) return null;
        items.RemoveAll(a => a.Domain == d);
        items.Add(new TimedAllow { Domain = d, ExpiresUtc = expiresUtc });
        return d;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Self-check: one plain verdict from a handful of facts.
// ---------------------------------------------------------------------------------------------------------------

public enum HealthLevel { Unknown, Protected, Partial, NotProtected }

public sealed record HealthFacts(bool ProtectionOn, bool Paused, bool ProxyListening, int AdaptersTotal, int AdaptersUsingUs, bool SampleBlocked, bool UpstreamResolves);

public sealed record HealthResult(HealthLevel Level, string Summary, List<string> Problems)
{
    public string Word => Level switch { HealthLevel.Protected => "Protected", HealthLevel.Partial => "Partly protected", HealthLevel.NotProtected => "Not protected", _ => "Not checked" };
}

public static class Health
{
    public static HealthResult Evaluate(HealthFacts f)
    {
        var problems = new List<string>();
        if (!f.ProtectionOn) return new(HealthLevel.NotProtected, "Protection is off.", ["Protection is turned off."]);
        if (f.Paused) return new(HealthLevel.Partial, "Protection is paused.", ["Protection is paused; lookups are not being filtered."]);
        if (!f.ProxyListening) return new(HealthLevel.NotProtected, "Hushwire is not answering DNS lookups.", ["The local DNS filter is not running."]);
        bool noneUse = f.AdaptersTotal > 0 && f.AdaptersUsingUs == 0;
        if (noneUse) problems.Add("No network adapter is using Hushwire. Another program or a VPN may have changed DNS.");
        else if (f.AdaptersUsingUs < f.AdaptersTotal) problems.Add($"{f.AdaptersTotal - f.AdaptersUsingUs} of {f.AdaptersTotal} network adapters are not using Hushwire, so their lookups are not filtered.");
        if (!f.SampleBlocked) problems.Add("A known ad domain was not blocked. The filter lists may be empty or still downloading.");
        if (!f.UpstreamResolves) problems.Add("Normal websites did not resolve through Hushwire. Check the upstream DNS setting.");
        var level = noneUse ? HealthLevel.NotProtected : problems.Count == 0 ? HealthLevel.Protected : HealthLevel.Partial;
        string summary = level == HealthLevel.Protected ? "Everything checked is working." : problems[0];
        return new(level, summary, problems);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Profiles (which lists are on) and schedules (switch profile or pause at set times).
// ---------------------------------------------------------------------------------------------------------------

public sealed record ListProfile(string Id, string Name, string Description, string[] ListIds);

public static class ListProfiles
{
    static readonly string[] Standard = ["stevenblack", "easyprivacy", "urlhaus"];

    public static readonly ListProfile[] All =
    [
        new("light", "Light", "Fewest blocked lookups, least chance of breaking a site.", ["stevenblack"]),
        new("standard", "Standard", "Ads, trackers and known malware. The default.", Standard),
        new("strict", "Strict", "Adds HaGeZi Pro and OISD small. Blocks more; a few sites may need an allow rule.", [.. Standard, "hagezi-pro", "oisd-small"]),
    ];

    public static ListProfile? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

public sealed class ScheduleRule
{
    public string Name { get; set; } = "";
    /// <summary>Bit 0 = Monday … bit 6 = Sunday.</summary>
    public int Days { get; set; } = 127;
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    /// <summary>"Pause", or "Profile:" followed by a profile id.</summary>
    public string Action { get; set; } = "Pause";
    public bool Enabled { get; set; } = true;
}

public sealed record ScheduleOutcome(bool Pause, string? Profile);

public static class Schedules
{
    static int DayBit(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    public static bool Matches(ScheduleRule r, DateTime local)
    {
        if (!r.Enabled) return false;
        int minute = local.Hour * 60 + local.Minute;
        int today = DayBit(local.DayOfWeek), yesterday = DayBit(local.AddDays(-1).DayOfWeek);
        int s = r.StartMinute, e = r.EndMinute;
        if (s == e) return (r.Days & today) != 0;                                     // same start and end: the whole day
        if (s < e) return (r.Days & today) != 0 && minute >= s && minute < e;
        // wraps past midnight: the evening part belongs to the day it started on, the morning part to the day after
        return ((r.Days & today) != 0 && minute >= s) || ((r.Days & yesterday) != 0 && minute < e);
    }

    /// <summary>A pause rule wins over a profile rule; of several profile rules the last one in the list wins.</summary>
    public static ScheduleOutcome Evaluate(IEnumerable<ScheduleRule> rules, DateTime local)
    {
        bool pause = false; string? profile = null;
        foreach (var r in rules.Where(r => Matches(r, local)))
        {
            if (r.Action == "Pause") pause = true;
            else if (r.Action.StartsWith("Profile:", StringComparison.Ordinal) && ListProfiles.Find(r.Action[8..]) is { } p) profile = p.Id;
        }
        return new(pause, profile);
    }

    public static string DaysText(int days)
    {
        if (days == 127) return "every day";
        if (days == 31) return "weekdays";
        if (days == 96) return "weekends";
        string[] n = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        return string.Join(", ", Enumerable.Range(0, 7).Where(i => (days & (1 << i)) != 0).Select(i => n[i]));
    }

    public static string TimeText(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    public static string Describe(ScheduleRule r)
    {
        string what = r.Action == "Pause" ? "pause protection" : $"use the {ListProfiles.Find(r.Action.Replace("Profile:", ""))?.Name ?? "?"} level";
        string when = r.StartMinute == r.EndMinute ? "all day" : $"{TimeText(r.StartMinute)}–{TimeText(r.EndMinute)}";
        return $"{(r.Name.Length > 0 ? r.Name + ": " : "")}{what}, {DaysText(r.Days)}, {when}";
    }
}
