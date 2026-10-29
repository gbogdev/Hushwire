using System.Text;
using Hushwire.Core;
using Hushwire.Core.AdFilter;
using Xunit;

namespace Hushwire.Tests;

public class NetScriptsTests
{
    const string Guid1 = "{6f1c2b0e-3a4d-4c55-9a0b-0d5e8d1f2a11}";

    [Theory]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData("a\u2018b", "a\u2018\u2018b")]      // typographic quotes also end a PowerShell string
    [InlineData("a\u2019;calc;\u2019b", "a\u2019\u2019;calc;\u2019\u2019b")]
    public void AdapterNameQuotesAreEscaped(string name, string expected) => Assert.Equal(expected, NetScripts.Q(name));

    [Fact]
    public void ScriptsNeverContainAnUnescapedQuoteFromTheAdapterName()
    {
        var a = new AdapterState(Guid1, "Wi-Fi\u2019; Remove-Item X ;\u2019", true, ["192.168.1.1"]);
        foreach (var script in new[] { NetScripts.Apply([a], false), NetScripts.Restore(new Journal { Adapters = [a] }) })
            Assert.DoesNotContain("Wi-Fi\u2019;", script);   // every \u2019 from the name must appear doubled
    }
}

public class LocalCaTests
{
    [Fact]
    public void ReportsExpiryAboutTwoYearsOut()
    {
        using var ca = LocalCa.Create();
        Assert.InRange(ca.NotAfter, DateTime.Now.AddDays(700), DateTime.Now.AddDays(740));
    }

    [Theory]
    [InlineData("www.youtube.com", true)]
    [InlineData("usher.ttvnw.net", true)]
    [InlineData("evilyoutube.com", false)]
    [InlineData("youtube.com.evil.example", false)]
    [InlineData("bank.example", false)]
    public void OnlyPermittedHostsAreIssued(string host, bool ok)
    {
        using var ca = LocalCa.Create();
        if (ok) { using var c = ca.Issue(host); Assert.Contains(host, c.Subject); }
        else Assert.Throws<ArgumentException>(() => ca.Issue(host));
    }
}

public class ListValidationTests
{
    static ListManager.Validation V(string text, int previous = 0) => ListManager.Validate(Encoding.UTF8.GetBytes(text), previous);

    [Fact] public void HtmlPageIsRejected() => Assert.NotNull(V("<!DOCTYPE html><html></html>").Error);
    [Fact] public void BinaryIsRejected() => Assert.NotNull(ListManager.Validate([0x61, 0x00, 0x62], 0).Error);
    [Fact] public void HostsListIsAccepted() { var v = V("0.0.0.0 ads.example.com\n0.0.0.0 track.example.net\n"); Assert.Null(v.Error); Assert.Equal(2, v.Valid); }
    [Fact]
    public void SuddenShrinkIsRejected()
    {
        var v = V("0.0.0.0 ads.example.com\n", previous: 100_000);
        Assert.NotNull(v.Error);
    }
}
