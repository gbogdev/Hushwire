using System.Runtime.CompilerServices;
using System.Text.Json;
using Hushwire.App;
using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

static class TestData
{
    /// <summary>Must run before anything touches <see cref="Paths"/>: the data folder is fixed at first use.</summary>
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("HUSHWIRE_DATA", Path.Combine(Path.GetTempPath(), "hushwire-tests-" + Environment.ProcessId));
}

sealed class FakeBackend : INetBackend
{
    public bool NativeApplyOk, NativeRestoreOk;
    public int PsExitCode;
    public string PsOutput = "";
    public int NativeApplyCalls, NativeRestoreCalls;
    public List<string> Scripts = [];
    public bool TryApply(List<AdapterState> a, bool ipv6) { NativeApplyCalls++; return NativeApplyOk; }
    public bool TryRestore(Journal j) { NativeRestoreCalls++; return NativeRestoreOk; }
    public Task<(int Code, string Output)> RunPowerShellAsync(string script) { Scripts.Add(script); return Task.FromResult((PsExitCode, PsOutput)); }
}

[Collection("data folder")]
public class NetConfigRestoreTests : IDisposable
{
    const string Guid1 = "{6f1c2b0e-3a4d-4c55-9a0b-0d5e8d1f2a11}";
    static readonly Journal OneAdapter = new() { Adapters = [new AdapterState(Guid1, "Wi-Fi", true, ["192.168.1.1"])] };

    public NetConfigRestoreTests() { Directory.CreateDirectory(Paths.Dir); Clean(); }
    public void Dispose() => Clean();
    static void Clean() { foreach (var f in Directory.GetFiles(Paths.Dir, "netstate.json*")) File.Delete(f); }
    static void WriteJournal() => File.WriteAllText(Paths.JournalFile, JsonSerializer.Serialize(OneAdapter));

    [Fact]
    public async Task NoJournalMeansNothingToDo()
    {
        var b = new FakeBackend();
        await new NetConfig(b).RestoreAsync();
        Assert.Equal(0, b.NativeRestoreCalls);
        Assert.Empty(b.Scripts);
    }

    [Fact]
    public async Task SuccessfulNativeRestoreRemovesTheJournal()
    {
        WriteJournal();
        var b = new FakeBackend { NativeRestoreOk = true };
        await new NetConfig(b).RestoreAsync();
        Assert.False(File.Exists(Paths.JournalFile));
        Assert.Empty(b.Scripts);
    }

    [Fact]
    public async Task WhenTheNativeCallFailsPowerShellTakesOver()
    {
        WriteJournal();
        var b = new FakeBackend { NativeRestoreOk = false, PsExitCode = 0 };
        await new NetConfig(b).RestoreAsync();
        Assert.Single(b.Scripts);
        Assert.Contains("ResetServerAddresses", b.Scripts[0]);
        Assert.False(File.Exists(Paths.JournalFile));
    }

    [Fact]
    public async Task AFailedRestoreKeepsTheJournalSoItCanBeRetried()
    {
        WriteJournal();
        var b = new FakeBackend { NativeRestoreOk = false, PsExitCode = 1, PsOutput = "access denied" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new NetConfig(b).RestoreAsync());
        Assert.Contains("access denied", ex.Message);
        Assert.True(File.Exists(Paths.JournalFile));
    }

    [Fact]
    public async Task ACorruptJournalIsSetAsideNotDeletedAndNotApplied()
    {
        File.WriteAllText(Paths.JournalFile, "{ this is not json");
        var b = new FakeBackend();
        await new NetConfig(b).RestoreAsync();
        Assert.Equal(0, b.NativeRestoreCalls);
        Assert.Empty(b.Scripts);
        Assert.True(File.Exists(Paths.JournalFile + ".corrupt"));
        Assert.False(File.Exists(Paths.JournalFile));
    }

    [Fact]
    public async Task ALockedJournalIsNotMistakenForNoJournal()
    {
        WriteJournal();
        var b = new FakeBackend { NativeRestoreOk = true };
        using (new FileStream(Paths.JournalFile, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => new NetConfig(b).RestoreAsync());
        Assert.True(File.Exists(Paths.JournalFile));      // the original DNS settings are still recorded
        Assert.Equal(0, b.NativeRestoreCalls);
    }

    [Fact]
    public async Task ApplyUsesTheNativeCallFirstAndPowerShellOnlyAsFallback()
    {
        var a = OneAdapter.Adapters;
        var native = new FakeBackend { NativeApplyOk = true };
        await new NetConfig(native).ApplyAsync(a, true);
        Assert.Empty(native.Scripts);

        var fallback = new FakeBackend { NativeApplyOk = false, PsExitCode = 0 };
        await new NetConfig(fallback).ApplyAsync(a, true);
        Assert.Single(fallback.Scripts);

        var broken = new FakeBackend { NativeApplyOk = false, PsExitCode = 1, PsOutput = "adapter not found" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NetConfig(broken).ApplyAsync(a, true));
    }
}

[CollectionDefinition("data folder", DisableParallelization = true)]
public class DataFolderCollection { }

public class RestoreScriptTests
{
    const string Guid1 = "{6f1c2b0e-3a4d-4c55-9a0b-0d5e8d1f2a11}";

    [Fact]
    public void RestoreOnlyTouchesAdaptersThatStillPointAtUs()
    {
        var j = new Journal { Adapters = [new AdapterState(Guid1, "Wi-Fi", true, ["192.168.1.1", "fd00::1"])] };
        var script = NetScripts.Restore(j);
        Assert.Contains("-contains '127.0.0.1'", script);
        Assert.Contains("'192.168.1.1'", script);
        Assert.Contains("'fd00::1'", script);
        Assert.True(script.IndexOf("-contains '127.0.0.1'", StringComparison.Ordinal) < script.IndexOf("ResetServerAddresses", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticAdaptersAreOnlyReset()
    {
        var j = new Journal { Adapters = [new AdapterState(Guid1, "Ethernet", false, ["192.168.1.1"])] };
        var script = NetScripts.Restore(j);
        Assert.Contains("ResetServerAddresses", script);
        Assert.DoesNotContain("'192.168.1.1'", script);
    }

    [Fact]
    public void ABadGuidOrAddressInAJournalNeverReachesTheScript()
    {
        var j = new Journal { Adapters = [new AdapterState("not-a-guid'; calc; '", "x", true, ["1.1.1.1'; calc; '"])] };
        var script = NetScripts.Restore(j);
        Assert.DoesNotContain("calc", script);
    }
}
