using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Hushwire.Core;

namespace Hushwire.App;

/// <summary>Everyday helpers: notices, the "something broke" fixer, timed allows, the self-check, schedules and profiles.</summary>
public sealed partial class Protection
{
    // ---------------------------------------------------------------- notices

    /// <summary>(title, message) for something that needs attention. The tray shows it as a Windows notification.</summary>
    public event Action<string, string>? Notice;
    readonly Dictionary<string, DateTime> noticeSent = [];

    /// <summary>Raises a notice unless notifications are off or the same kind was shown in the last 30 minutes.</summary>
    void Notify(string key, string title, string message)
    {
        if (!S.Notifications) return;
        lock (noticeSent)
        {
            if (noticeSent.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30)) return;
            noticeSent[key] = DateTime.UtcNow;
        }
        Notice?.Invoke(title, message);
    }

    // ---------------------------------------------------------------- housekeeping (every 5 s)

    Timer? houseTimer;
    int houseBusy;
    long lastScheduleTick, lastHealthTick;
    volatile string? lastForeground;

    void StartHousekeeping()
    {
        appliedProfile = S.Profile;
        houseTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref houseBusy, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try { await HousekeepAsync(); }
                catch (Exception e) { App.ErrorLog.Write(e); }
                finally { Volatile.Write(ref houseBusy, 0); }
            });
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    async Task HousekeepAsync()
    {
        if (Attribution.ForegroundName() is { } fg) lastForeground = fg;
        if (TimedAllows.Prune(S.TimedAllows, DateTime.UtcNow)) { Save(); await ApplyRulesAsync(); }
        long now = Environment.TickCount64;
        if (now - lastScheduleTick >= 30_000) { lastScheduleTick = now; await ScheduleTickAsync(); }
        if (now - lastHealthTick >= 600_000 && State == ProtectionState.On) { lastHealthTick = now; await SelfCheckAsync(); }
    }

    // ---------------------------------------------------------------- allow rules including timed ones

    /// <summary>Your allowlist plus the temporary allows that have not expired yet.</summary>
    List<string> EffectiveAllow() => S.UserAllow.Concat(TimedAllows.Active(S.TimedAllows, DateTime.UtcNow)).Distinct().ToList();

    public async Task AllowTemporarilyAsync(string domain, TimeSpan span)
    {
        if (TimedAllows.Add(S.TimedAllows, domain, DateTime.UtcNow + span) is null) return;
        await ApplyRulesAsync();        // saves and rebuilds the rules at once
    }

    public async Task AllowPermanentlyAsync(string domain)
    {
        var d = Domain.Normalize(domain);
        if (d is null) return;
        S.UserBlock.Remove(d);
        if (!S.UserAllow.Contains(d)) S.UserAllow.Add(d);
        S.TimedAllows.RemoveAll(a => a.Domain == d);
        await ApplyRulesAsync();
    }

    // ---------------------------------------------------------------- "something broke"

    /// <summary>Blocked lookups from the last ten minutes, most likely culprit first.</summary>
    public List<Suspect> FindSuspects()
    {
        List<BlockHit> hits;
        lock (actGate)
            hits = activity.Where(r => r.Result == "Blocked" && DateTime.Now - r.Time < TimeSpan.FromMinutes(10)).Select(r => new BlockHit(r.Domain, r.App, r.Time)).ToList();
        var already = TimedAllows.Active(S.TimedAllows, DateTime.UtcNow).ToHashSet();
        return BreakageRanker.Rank(hits, DateTime.Now, lastForeground).Where(s => !already.Contains(s.Domain)).ToList();
    }

    // ---------------------------------------------------------------- self-check

    public HealthResult? LastHealth { get; private set; }

    public async Task<HealthResult> SelfCheckAsync()
    {
        bool on = State is ProtectionState.On or ProtectionState.Paused;
        int total = 0, ours = 0;
        if (!Dev)
            foreach (var n in NetConfig.Eligible())
            {
                total++;
                var dns = n.GetIPProperties().DnsAddresses;
                if (dns.Count > 0 && dns.All(IPAddress.IsLoopback)) ours++;
            }
        bool sampleBlocked = true, resolves = true;
        if (proxy is not null && State == ProtectionState.On)
        {
            string? sample = new[] { "doubleclick.net", "googleadservices.com", "ads.google.com", "adservice.google.com" }.FirstOrDefault(x => snapshot.Decide(x).Blocked);
            sampleBlocked = sample is null ? snapshot.RuleCount > 0 : await Probe(sample) is { nullIp: true };
            resolves = await Probe("example.com") is { rcode: 0 };
        }
        var result = Health.Evaluate(new HealthFacts(on, State == ProtectionState.Paused, proxy is not null, total, ours, sampleBlocked, resolves));
        var before = LastHealth;
        LastHealth = result;
        if (State == ProtectionState.On && result.Level is HealthLevel.Partial or HealthLevel.NotProtected && (before is null || before.Level != result.Level))
            Notify("health", "Hushwire needs attention", result.Summary);
        Raise();
        return result;
    }

    // ---------------------------------------------------------------- pause until restart

    bool pausedBySchedule, scheduleSuppressed;

    /// <summary>Paused with no end time: protection comes back the next time Hushwire starts (or when you resume).</summary>
    public void PauseUntilRestart(string detail = "Paused until you resume or restart Hushwire")
    {
        if (State is not (ProtectionState.On or ProtectionState.Paused)) return;
        paused = true; PausedUntil = null;
        pauseTimer?.Dispose();
        proxy?.Cache.Clear();
        if (!Dev) DnsApi.Flush();
        Set(ProtectionState.Paused, detail);
    }

    /// <summary>Ctrl+Alt+H: pause for 30 minutes, or resume if already paused.</summary>
    public void TogglePauseHotkey()
    {
        if (State == ProtectionState.Paused) Resume();
        else if (State == ProtectionState.On) Pause(TimeSpan.FromMinutes(30));
    }

    // ---------------------------------------------------------------- profiles and schedules

    string appliedProfile = "standard";
    HashSet<string>? savedEnabled;           // the lists that were on before a schedule switched profile

    /// <summary>Turns the profile's lists on and the other built-in lists off (your own lists are untouched). Downloads any that are missing.</summary>
    public async Task SetProfileAsync(string id)
    {
        var p = ListProfiles.Find(id);
        if (p is null) return;
        S.Profile = p.Id; appliedProfile = p.Id; savedEnabled = null;
        await ApplyListSetAsync(p.ListIds);
    }

    async Task ApplyListSetAsync(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        foreach (var l in S.Lists.Where(l => l.Builtin)) l.Enabled = set.Contains(l.Id);
        Save();
        await UpdateListsAsync(false);
        await RebuildAsync();
    }

    async Task ScheduleTickAsync()
    {
        var o = Schedules.Evaluate(S.Schedules, DateTime.Now);

        if (!o.Pause) scheduleSuppressed = false;
        if (o.Pause && !scheduleSuppressed && State == ProtectionState.On && !pausedBySchedule)
        {
            pausedBySchedule = true;
            PauseUntilRestart("Paused by a schedule; it resumes when the schedule ends");
            Notify("schedule", "Protection paused", "A schedule paused protection. It resumes by itself when the schedule ends.");
        }
        else if (!o.Pause && pausedBySchedule)
        {
            pausedBySchedule = false;
            if (State == ProtectionState.Paused) { Resume(); Notify("schedule-end", "Protection resumed", "The schedule ended and protection is back on."); }
        }

        if (o.Profile is { } want && want != appliedProfile)
        {
            savedEnabled ??= S.Lists.Where(l => l.Enabled).Select(l => l.Id).ToHashSet();
            appliedProfile = want;
            await ApplyListSetAsync(ListProfiles.Find(want)!.ListIds);
        }
        else if (o.Profile is null && savedEnabled is not null)
        {
            var back = savedEnabled; savedEnabled = null; appliedProfile = S.Profile;
            foreach (var l in S.Lists) l.Enabled = back.Contains(l.Id);
            Save();
            await UpdateListsAsync(false);
            await RebuildAsync();
        }
    }
}
