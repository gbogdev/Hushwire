using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

public class BreakageRankerTests
{
    static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0);
    static BlockHit H(string d, string app, double minutesAgo) => new(d, app, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void TheProgramInFrontBeatsBackgroundNoise()
    {
        var r = BreakageRanker.Rank([H("bg.example.net", "Windows DNS Client", 0.1), H("cdn.shop.com", "chrome.exe", 2)], Now, "chrome.exe");
        Assert.Equal("cdn.shop.com", r[0].Domain);
        Assert.Contains("in front", r[0].Reason);
    }

    [Fact]
    public void OldHitsAreLeftOut()
    {
        var r = BreakageRanker.Rank([H("old.example.com", "a", 30), H("new.example.com", "a", 1)], Now, null);
        Assert.Single(r);
        Assert.Equal("new.example.com", r[0].Domain);
    }

    [Fact]
    public void NewerBeatsOlderWhenNothingElseDiffers()
    {
        var r = BreakageRanker.Rank([H("a.example.com", "x", 8), H("b.example.com", "x", 1)], Now, null);
        Assert.Equal("b.example.com", r[0].Domain);
    }

    [Fact]
    public void ARepeatedTelemetryLoopRanksBelowAOneOffBlock()
    {
        var loop = Enumerable.Range(0, 60).Select(i => H("telemetry.example.com", "svc", 1));
        var r = BreakageRanker.Rank(loop.Append(H("login.example.com", "svc", 1)), Now, null);
        Assert.Equal("login.example.com", r[0].Domain);
    }

    [Fact]
    public void GroupsByDomainCountsAndRespectsTheLimit()
    {
        var hits = Enumerable.Range(0, 20).Select(i => H($"d{i}.example.com", "x", 1)).Concat([H("d0.example.com", "x", 0.5), H("D0.example.com", "x", 0.2)]);
        var r = BreakageRanker.Rank(hits, Now, null, max: 5);
        Assert.Equal(5, r.Count);
        Assert.Equal(3, r.First(s => s.Domain.Equals("d0.example.com", StringComparison.OrdinalIgnoreCase)).Hits);
    }

    [Fact] public void NothingBlockedMeansNoSuspects() => Assert.Empty(BreakageRanker.Rank([], Now, "x"));
}

public class TimedAllowTests
{
    static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnAllowIsActiveUntilItExpiresThenGone()
    {
        var l = new List<TimedAllow>();
        Assert.Equal("shop.example.com", TimedAllows.Add(l, "Shop.Example.com", Now.AddHours(1)));
        Assert.Contains("shop.example.com", TimedAllows.Active(l, Now));
        Assert.Contains("shop.example.com", TimedAllows.Active(l, Now.AddMinutes(59)));
        Assert.Empty(TimedAllows.Active(l, Now.AddHours(1)));
        Assert.True(TimedAllows.Prune(l, Now.AddHours(1)));
        Assert.Empty(l);
        Assert.False(TimedAllows.Prune(l, Now.AddHours(2)));
    }

    [Fact]
    public void AddingTheSameDomainExtendsInsteadOfDuplicating()
    {
        var l = new List<TimedAllow>();
        TimedAllows.Add(l, "a.example.com", Now.AddMinutes(10));
        TimedAllows.Add(l, "a.example.com", Now.AddHours(3));
        Assert.Single(l);
        Assert.Equal(Now.AddHours(3), l[0].ExpiresUtc);
    }

    [Fact]
    public void InvalidNamesAreIgnoredAndBadStoredEntriesPruned()
    {
        var l = new List<TimedAllow> { new() { Domain = "not a domain!", ExpiresUtc = Now.AddDays(1) } };
        Assert.Null(TimedAllows.Add(l, "", Now.AddHours(1)));
        Assert.True(TimedAllows.Prune(l, Now));
        Assert.Empty(l);
    }
}

public class HealthTests
{
    static HealthFacts Good => new(true, false, true, 2, 2, true, true);

    [Fact] public void AllGoodIsProtected() => Assert.Equal(HealthLevel.Protected, Health.Evaluate(Good).Level);
    [Fact] public void OffIsNotProtected() => Assert.Equal(HealthLevel.NotProtected, Health.Evaluate(Good with { ProtectionOn = false }).Level);
    [Fact] public void PausedIsPartial() => Assert.Equal(HealthLevel.Partial, Health.Evaluate(Good with { Paused = true }).Level);
    [Fact] public void NoListenerIsNotProtected() => Assert.Equal(HealthLevel.NotProtected, Health.Evaluate(Good with { ProxyListening = false }).Level);
    [Fact] public void NoAdapterUsingUsIsNotProtected() => Assert.Equal(HealthLevel.NotProtected, Health.Evaluate(Good with { AdaptersUsingUs = 0 }).Level);

    [Fact]
    public void SomeAdaptersMissingIsPartialAndSaysHowMany()
    {
        var r = Health.Evaluate(Good with { AdaptersUsingUs = 1 });
        Assert.Equal(HealthLevel.Partial, r.Level);
        Assert.Contains("1 of 2", r.Summary);
    }

    [Fact] public void AnAdDomainThatIsNotBlockedIsPartial() => Assert.Equal(HealthLevel.Partial, Health.Evaluate(Good with { SampleBlocked = false }).Level);
    [Fact] public void BrokenUpstreamIsPartial() => Assert.Equal(HealthLevel.Partial, Health.Evaluate(Good with { UpstreamResolves = false }).Level);
    [Fact] public void DeveloperModeWithNoAdaptersCanStillBeProtected() => Assert.Equal(HealthLevel.Protected, Health.Evaluate(Good with { AdaptersTotal = 0, AdaptersUsingUs = 0 }).Level);
}

public class ScheduleTests
{
    // 2026-10-30 is a Friday
    static DateTime At(int day, int h, int m) => new(2026, 10, 26 + day, h, m, 0);     // day 0 = Monday 26 Oct
    static ScheduleRule Rule(int days, int start, int end, string action = "Pause") => new() { Days = days, StartMinute = start, EndMinute = end, Action = action };

    [Fact]
    public void ADaytimeWindowIncludesItsStartAndExcludesItsEnd()
    {
        var r = Rule(127, 9 * 60, 17 * 60);
        Assert.False(Schedules.Matches(r, At(0, 8, 59)));
        Assert.True(Schedules.Matches(r, At(0, 9, 0)));
        Assert.True(Schedules.Matches(r, At(0, 16, 59)));
        Assert.False(Schedules.Matches(r, At(0, 17, 0)));
    }

    [Fact]
    public void ANightWindowWrapsPastMidnightAndBelongsToTheDayItStarted()
    {
        var fridayNight = Rule(1 << 4, 22 * 60, 6 * 60);     // Friday 22:00 to Saturday 06:00
        Assert.True(Schedules.Matches(fridayNight, At(4, 23, 0)));      // Friday 23:00
        Assert.True(Schedules.Matches(fridayNight, At(5, 5, 59)));      // Saturday 05:59
        Assert.False(Schedules.Matches(fridayNight, At(5, 6, 0)));
        Assert.False(Schedules.Matches(fridayNight, At(4, 5, 0)));      // Friday early morning is Thursday's night
        Assert.False(Schedules.Matches(fridayNight, At(5, 23, 0)));     // Saturday night
    }

    [Fact]
    public void OnlyTheChosenDaysMatch()
    {
        var weekdays = Rule(31, 0, 0);       // same start and end: all day
        Assert.True(Schedules.Matches(weekdays, At(2, 12, 0)));
        Assert.False(Schedules.Matches(weekdays, At(5, 12, 0)));
    }

    [Fact] public void DisabledRulesNeverMatch() => Assert.False(Schedules.Matches(new ScheduleRule { Enabled = false, Days = 127, StartMinute = 0, EndMinute = 0 }, At(0, 12, 0)));

    [Fact]
    public void PauseBeatsAProfileAndTheLastProfileWins()
    {
        var rules = new[] { Rule(127, 0, 0, "Profile:strict"), Rule(127, 0, 0, "Profile:light"), Rule(127, 12 * 60, 13 * 60) };
        var noon = Schedules.Evaluate(rules, At(0, 12, 30));
        Assert.True(noon.Pause);
        Assert.Equal("light", noon.Profile);
        var evening = Schedules.Evaluate(rules, At(0, 18, 0));
        Assert.False(evening.Pause);
    }

    [Fact]
    public void UnknownProfilesAreIgnored() => Assert.Null(Schedules.Evaluate([Rule(127, 0, 0, "Profile:nope")], At(0, 1, 0)).Profile);

    [Fact]
    public void RulesReadAsPlainSentences()
    {
        Assert.Equal("Work: pause protection, weekdays, 09:00–17:00", Schedules.Describe(new ScheduleRule { Name = "Work", Days = 31, StartMinute = 540, EndMinute = 1020 }));
        Assert.Contains("Strict level", Schedules.Describe(new ScheduleRule { Action = "Profile:strict", Days = 96 }));
    }

    [Fact]
    public void EveryProfileUsesRealCatalogLists()
    {
        var ids = ListCatalog.All().Select(l => l.Id).ToHashSet();
        Assert.All(ListProfiles.All, p => Assert.All(p.ListIds, id => Assert.Contains(id, ids)));
    }
}
