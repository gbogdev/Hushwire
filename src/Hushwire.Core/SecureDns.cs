namespace Hushwire.Core;

/// <summary>
/// Hostnames that browsers use to switch to their own encrypted DNS (DNS over HTTPS), which would bypass Hushwire.
/// When the option is on, Hushwire answers these with "does not exist": browsers in their default (automatic) mode then keep using
/// the system DNS, and Firefox treats its canary name as the signal to leave DoH off. A browser forced into strict secure-DNS mode
/// will show an error instead. Hushwire's own encrypted upstream connects by address and is not affected.
/// </summary>
public static class SecureDns
{
    /// <summary>Firefox asks for this name; "does not exist" tells it that the network wants DoH disabled.</summary>
    public const string Canary = "use-application-dns.net";

    static readonly string[] Names =
    [
        Canary,
        "dns.google", "cloudflare-dns.com", "dns.quad9.net", "dns.adguard-dns.com", "doh.opendns.com",
        "dns.nextdns.io", "doh.cleanbrowsing.org", "doh.libredns.gr", "dns.sb",
    ];

    /// <summary>True for the names above and any subdomain of them (e.g. mozilla.cloudflare-dns.com).</summary>
    public static bool Matches(string name)
    {
        foreach (var n in Names)
            if (name.Length >= n.Length && name.EndsWith(n, StringComparison.Ordinal) && (name.Length == n.Length || name[name.Length - n.Length - 1] == '.'))
                return true;
        return false;
    }
}
