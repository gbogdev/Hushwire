using Hushwire.App;
using Hushwire.Core;
using Xunit;

namespace Hushwire.Tests;

public class RedactorTests
{
    [Fact]
    public void PublicAndPrivateIpv4AddressesAreMasked()
    {
        var r = Redactor.Redact("upstream 192.168.1.254, 8.8.4.4 and 127.0.0.1 and 0.0.0.0");
        Assert.Contains("192.168.x.x", r);
        Assert.Contains("8.8.x.x", r);
        Assert.DoesNotContain("1.254", r);
        Assert.Contains("127.0.0.1", r);
        Assert.Contains("0.0.0.0", r);
    }

    [Fact]
    public void Ipv6AddressesAreMaskedButLoopbackIsKept()
    {
        var r = Redactor.Redact("dns 2a02:2f0c:8000:3::1 fd00::1 fe80::1ff:fe23:4567:890a and ::1");
        Assert.DoesNotContain("2a02", r);
        Assert.DoesNotContain("fd00", r);
        Assert.DoesNotContain("fe80", r);
        Assert.EndsWith("and ::1", r);
    }

    [Fact]
    public void WindowsUserNameIsMaskedInPaths()
    {
        var r = Redactor.Redact(@"at C:\Users\Jane Doe\AppData\Local\x.dll and c:\users\bob\file.txt");
        Assert.DoesNotContain("Jane", r);
        Assert.DoesNotContain("bob", r);
        Assert.Contains(@"C:\Users\<user>\AppData", r);
    }

    [Fact]
    public void TextWithNothingPersonalIsUnchanged()
    {
        const string s = "State: On  ·  rules: 121,259  ·  version 0.1.3  ·  at 12:30:45 and 2026-10-30T12:30:45";
        Assert.Equal(s, Redactor.Redact(s));
        Assert.Equal("", Redactor.Redact(""));
    }
}

public class RotatingLogTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "hushwire-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void TheLogNeverGrowsPastTwiceItsLimit()
    {
        var log = new RotatingLog(Path.Combine(dir, "e.log"), 2_000);
        for (int i = 0; i < 500; i++) log.Write(new string('x', 100) + i);
        long total = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
        Assert.InRange(total, 1, 4_500);
        Assert.True(File.Exists(Path.Combine(dir, "e.log.1")));
    }

    [Fact]
    public void TailReturnsTheNewestEntries()
    {
        var log = new RotatingLog(Path.Combine(dir, "e.log"), 1_000_000);
        for (int i = 0; i < 1000; i++) log.Write("entry " + i);
        var tail = log.Tail(500);
        Assert.True(tail.Length <= 500);
        Assert.Contains("entry 999", tail);
        Assert.DoesNotContain("entry 0\r", tail);
    }

    [Fact]
    public void WritingToAnUnwritableLocationNeverThrows()
    {
        var log = new RotatingLog(Path.Combine(dir, "sub", "e.log"), 100);
        File.WriteAllText(dir.TrimEnd('\\') + "-file", "x");                      // a file where a folder is needed
        var bad = new RotatingLog(Path.Combine(dir + "-file", "e.log"), 100);
        bad.Write("hello");
        bad.Write(new InvalidOperationException("boom"));
        Assert.Equal("", bad.Tail());
        File.Delete(dir.TrimEnd('\\') + "-file");
    }

    [Fact]
    public async Task ConcurrentWritersDoNotCorruptOrThrow()
    {
        var log = new RotatingLog(Path.Combine(dir, "e.log"), 5_000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() => { for (int i = 0; i < 200; i++) log.Write($"t{t} line {i}"); })));
        Assert.NotEqual("", log.Tail());
    }
}
