namespace Hushwire.Core;

/// <summary>
/// The built-in list catalog. Lists are downloaded by the user's PC from each publisher at runtime (Hushwire does not
/// redistribute them), so a list's license only matters to how it may be used — shown in the UI. Every URL here was
/// fetched and parsed with Hushwire's own parser when added; sizes are rule counts at that time.
/// </summary>
public static class ListCatalog
{
    const string Raw = "https://raw.githubusercontent.com/";

    static ListSub L(string id, string name, string category, string license, string url, string description, bool on = false) =>
        new() { Id = id, Name = name, Category = category, License = license, Url = url, Description = description, Enabled = on, Builtin = true };

    public static List<ListSub> All() =>
    [
        // ---- on by default
        L("stevenblack", "StevenBlack Unified Hosts", "Ads", "MIT", Raw + "StevenBlack/hosts/master/hosts",
            "Well-known combined ads, trackers and malware list (~71k). A safe base.", on: true),
        L("easyprivacy", "EasyPrivacy (hosts format)", "Trackers", "GPLv3 / CC BY-SA", "https://v.firebog.net/hosts/Easyprivacy.txt",
            "Tracking and analytics domains from the EasyList project (~43k).", on: true),
        L("urlhaus", "URLhaus malware domains", "Malware", "see publisher", "https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-hosts.txt",
            "Domains currently serving malware, from abuse.ch (~2k). Updated often.", on: true),

        // ---- ads
        L("hagezi-light", "HaGeZi Light", "Ads", "GPLv3", Raw + "hagezi/dns-blocklists/main/wildcard/light-onlydomains.txt",
            "Ads, trackers and malware (~71k); very low risk of breaking sites. Includes subdomains."),
        L("hagezi-pro", "HaGeZi Pro", "Ads", "GPLv3", Raw + "hagezi/dns-blocklists/main/wildcard/pro-onlydomains.txt",
            "Stronger all-round list (~208k). Good balance of coverage and breakage; a popular choice."),
        L("oisd-small", "OISD small", "Ads", "see oisd.nl", "https://small.oisd.nl/domainswild2",
            "Carefully maintained ads and trackers list aimed at not breaking sites (~59k)."),
        L("oisd-big", "OISD big", "Ads", "see oisd.nl", "https://big.oisd.nl/domainswild2",
            "Bigger OISD list (~234k): more coverage, more chance something breaks."),
        L("adguarddns", "AdGuard DNS filter", "Ads", "GPLv3", "https://adguardteam.github.io/AdGuardSDNSFilter/Filters/filter.txt",
            "AdGuard's DNS list (~190k). Overlaps heavily with the others."),
        L("adaway", "AdAway hosts", "Ads", "CC BY 3.0", "https://adaway.org/hosts.txt",
            "Small, long-running ad servers list (~6.5k)."),
        L("pgl", "Peter Lowe's ad servers", "Ads", "see publisher", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext",
            "A classic ad-server list (~3.5k)."),
        L("anudeep", "Anudeep's ad servers", "Ads", "MIT", Raw + "anudeepND/blacklist/master/adservers.txt",
            "Ad servers list (~42k)."),
        L("onehosts-lite", "1Hosts Lite", "Ads", "MPL-2.0", "https://o0.pages.dev/Lite/hosts.txt",
            "Lightweight ads and trackers list (~3.7k) chosen to avoid false positives."),
        L("kadhosts", "KADhosts", "Ads", "CC BY-SA 4.0", Raw + "PolishFiltersTeam/KADhosts/master/KADhosts.txt",
            "Polish team's list of ads, scams and fraud (~41k); useful worldwide."),
        L("disconnect-ads", "Disconnect: advertising", "Ads", "GPLv3", "https://s3.amazonaws.com/lists.disconnect.me/simple_ad.txt",
            "The ad list used by Firefox tracking protection (~2.7k)."),
        L("admiral", "Admiral (anti-adblock) domains", "Ads", "see publisher", "https://v.firebog.net/hosts/Admiral.txt",
            "Domains of services that try to detect ad blockers (~1.7k)."),
        L("firebog-easylist", "EasyList (hosts format)", "Ads", "GPLv3 / CC BY-SA", "https://v.firebog.net/hosts/Easylist.txt",
            "Domains from EasyList, the most widely used ad list (~52k)."),

        // ---- trackers
        L("frogeye-cname", "First-party trackers (CNAME-cloaked)", "Trackers", "see publisher", "https://hostfiles.frogeye.fr/firstparty-trackers-hosts.txt",
            "Trackers hidden behind a site's own subdomain, which ordinary lists miss (~15k). Works with Hushwire's CNAME check."),
        L("perflyst-tv", "Smart TV tracking", "Trackers", "MIT", Raw + "Perflyst/PiHoleBlocklist/master/SmartTV.txt",
            "Telemetry and ads from smart TVs and streaming boxes (~500). Only useful if those devices use this PC's DNS."),
        L("perflyst-android", "Android tracking", "Trackers", "MIT", Raw + "Perflyst/PiHoleBlocklist/master/android-tracking.txt",
            "Tracking hosts used by Android apps (~80)."),

        // ---- Windows telemetry (can degrade Windows features; off by default)
        L("winspy", "WindowsSpyBlocker telemetry", "Telemetry", "MIT", Raw + "crazy-max/WindowsSpyBlocker/master/data/hosts/spy.txt",
            "Windows telemetry servers (~350). Turning this on may reduce Windows diagnostics, Store or update features."),
        L("hagezi-winoffice", "Windows/Office telemetry (HaGeZi)", "Telemetry", "GPLv3", Raw + "hagezi/dns-blocklists/main/wildcard/native.winoffice-onlydomains.txt",
            "Native Windows and Office tracking domains (~300). Same caution as above."),

        // ---- malware / phishing / scams
        L("phishing-army", "Phishing Army (extended)", "Malware", "CC BY-NC 4.0 (non-commercial)", "https://phishing.army/download/phishing_army_blocklist_extended.txt",
            "Large phishing domains list (~153k)."),
        L("malware-filter-phishing", "Phishing domains (malware-filter)", "Malware", "see publisher", "https://malware-filter.gitlab.io/malware-filter/phishing-filter-hosts.txt",
            "Active phishing domains (~27k). Updated often."),
        L("dandelion-malware", "DandelionSprout anti-malware", "Malware", "see publisher",
            Raw + "DandelionSprout/adfilt/master/Alternate%20versions%20Anti-Malware%20List/AntiMalwareHosts.txt",
            "Malware and scam domains (~12k)."),
        L("scamblocklist", "Scam blocklist", "Malware", "see publisher", Raw + "durablenapkin/scamblocklist/master/hosts.txt",
            "Fake shops and scam sites (~2k)."),
        L("spam404", "Spam404", "Malware", "see publisher", Raw + "Spam404/lists/master/main-blacklist.txt",
            "Spam, scam and fake-site domains (~8k)."),
        L("nocoin", "NoCoin (cryptojacking)", "Malware", "see publisher", Raw + "hoshsadiq/adblock-nocoin-list/master/hosts.txt",
            "Sites that mine cryptocurrency in your browser (~300)."),
        L("hagezi-tif-medium", "HaGeZi Threat Intelligence (medium)", "Malware", "GPLv3", Raw + "hagezi/dns-blocklists/main/wildcard/tif.medium-onlydomains.txt",
            "Large threat-intelligence feed (~560k rules, 10 MB). Uses a lot of memory; only for those who want maximum malware coverage."),
    ];
}
