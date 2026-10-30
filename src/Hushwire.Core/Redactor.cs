using System.Text.RegularExpressions;

namespace Hushwire.Core;

/// <summary>Removes personal details from text before it is saved to a file the user may send to someone (diagnostics export).</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"\b(?<a>\d{1,3})\.(?<b>\d{1,3})\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4();

    // anything that looks like it could be an IPv6 address; each candidate is then checked by the real parser, so times like 12:30:45 are left alone
    [GeneratedRegex(@"(?<![0-9A-Za-z:.])[0-9A-Fa-f:]*:[0-9A-Fa-f:]*:[0-9A-Fa-f:.]*(?![0-9A-Za-z:])")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\/:*?""<>|\r\n]+")]
    private static partial Regex UserFolder();

    /// <summary>Addresses keep their first two parts (enough to tell a home router from a public server); loopback and 0.0.0.0 are kept as they are.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = UserFolder().Replace(text, "$1<user>");
        text = Ipv4().Replace(text, m =>
            m.Value is "127.0.0.1" or "0.0.0.0" ? m.Value : $"{m.Groups["a"].Value}.{m.Groups["b"].Value}.x.x");
        text = Ipv6().Replace(text, m =>
            m.Value is "::1" or "::" || !System.Net.IPAddress.TryParse(m.Value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6
                ? m.Value : "[ipv6 redacted]");
        return text;
    }
}
