using System.Text.Json;
using Hushwire.App;
using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

[Collection("data folder")]
public class SettingsLoadTests : IDisposable
{
    public SettingsLoadTests() { Directory.CreateDirectory(Paths.Dir); Clean(); }
    public void Dispose() => Clean();
    static void Clean() { foreach (var f in Directory.GetFiles(Paths.Dir, "settings.json*")) File.Delete(f); }
    static Settings Load(string json) { File.WriteAllText(Paths.SettingsFile, json); return SettingsStore.Load(); }

    [Fact]
    public void NoFileMeansFirstRun()
    {
        var s = SettingsStore.Load();
        Assert.False(s.FirstRunDone);
        Assert.True(s.Notifications);
        Assert.True(s.PauseHotkey);
        Assert.Equal("standard", s.Profile);
    }

    [Fact]
    public void ASettingsFileFromBeforeTheWizardIsNotAFirstRun() => Assert.True(Load("{\"ProtectionWanted\":true}").FirstRunDone);

    [Fact]
    public void ABrandNewInstallThatSavedItsSettingsKeepsTheWizardState()
    {
        SettingsStore.Save(new Settings { FirstRunDone = false });
        Assert.False(SettingsStore.Load().FirstRunDone);
        SettingsStore.Save(new Settings { FirstRunDone = true });
        Assert.True(SettingsStore.Load().FirstRunDone);
    }

    [Fact]
    public void BadSchedulesAreDroppedAndGoodOnesClamped()
    {
        var s = Load(JsonSerializer.Serialize(new
        {
            FirstRunDone = true,
            Schedules = new object[]
            {
                new { Name = new string('x', 100), Days = 9999, StartMinute = -5, EndMinute = 99999, Action = "Pause", Enabled = true },
                new { Name = "bad action", Days = 1, StartMinute = 0, EndMinute = 10, Action = "FormatDisk", Enabled = true },
                new { Name = "bad profile", Days = 1, StartMinute = 0, EndMinute = 10, Action = "Profile:nope", Enabled = true },
                new { Name = "ok", Days = 31, StartMinute = 60, EndMinute = 120, Action = "Profile:strict", Enabled = true },
            },
        }));
        Assert.Equal(2, s.Schedules.Count);
        Assert.Equal(40, s.Schedules[0].Name.Length);
        Assert.InRange(s.Schedules[0].Days, 0, 127);
        Assert.Equal(0, s.Schedules[0].StartMinute);
        Assert.Equal(1439, s.Schedules[0].EndMinute);
        Assert.Equal("Profile:strict", s.Schedules[1].Action);
    }

    [Fact]
    public void ExpiredAndInvalidTimedAllowsAreRemovedOnLoad()
    {
        var s = Load(JsonSerializer.Serialize(new
        {
            FirstRunDone = true,
            TimedAllows = new object[]
            {
                new { Domain = "Still.Valid.com", ExpiresUtc = DateTime.UtcNow.AddHours(1) },
                new { Domain = "expired.example.com", ExpiresUtc = DateTime.UtcNow.AddHours(-1) },
                new { Domain = "not a domain!", ExpiresUtc = DateTime.UtcNow.AddHours(1) },
            },
        }));
        Assert.Single(s.TimedAllows);
        Assert.Equal("still.valid.com", s.TimedAllows[0].Domain);
    }

    [Fact]
    public void UnknownProfileAndProviderFallBackToSafeDefaults()
    {
        var s = Load("{\"FirstRunDone\":true,\"Profile\":\"nonsense\",\"EncryptedDns\":\"evil\"}");
        Assert.Equal("standard", s.Profile);
        Assert.Equal("Off", s.EncryptedDns);
    }

    [Fact]
    public void ACorruptFileGivesDefaultsAndKeepsACopy()
    {
        var s = Load("{ nope");
        Assert.Equal("standard", s.Profile);
        Assert.True(File.Exists(Paths.SettingsFile + ".bad"));
    }
}
