using System.Text;
using System.Net;

namespace Hushwire.Core;

public sealed record AdapterState(string Guid, string Name, bool Static, string[] Servers);

public sealed class Journal
{
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<AdapterState> Adapters { get; set; } = [];
}

/// <summary>PowerShell used to point adapters at the proxy and put them back. Pure string building so it is testable anywhere.</summary>
public static class NetScripts
{
    /// <summary>PowerShell also treats the typographic quotes ‘ ’ ‚ ‛ as single quotes, so an adapter name containing one could end the string early.</summary>
    internal static string Q(string s) => s.Replace("'", "''").Replace("‘", "‘‘").Replace("’", "’’").Replace("‚", "‚‚").Replace("‛", "‛‛");

    /// <summary>$a = the adapter, matched by GUID or name (null if Windows doesn't list it).</summary>
    static string Find(string guid, string name) =>
        $"$a=@(Get-NetAdapter | Where-Object {{ $_.InterfaceGuid -eq '{guid}' -or $_.Name -eq '{Q(name)}' }}) | Select-Object -First 1\n";

    public static string Apply(IEnumerable<AdapterState> adapters, bool ipv6)
    {
        var sb = new StringBuilder("$ErrorActionPreference='Stop'\n$ProgressPreference='SilentlyContinue'\ntry{\n");
        foreach (var a in adapters)
        {
            var g = System.Guid.Parse(a.Guid).ToString("B");
            sb.Append(Find(g, a.Name));
            // if Windows doesn't list the adapter, say what it does list so the problem is diagnosable
            sb.Append($"if(-not $a){{throw ('Network adapter not found: {g} / {Q(a.Name)}. Windows reports: ' + ((Get-NetAdapter | ForEach-Object {{ $_.Name + '=' + $_.InterfaceGuid }}) -join '; '))}}\n");
            sb.Append("Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses '127.0.0.1'\n");
            if (ipv6) sb.Append("Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses '::1'\n");
            // verify both families really took (setting one family must not have wiped the other)
            sb.Append("$c=@((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex).ServerAddresses)\nif($c -notcontains '127.0.0.1'){throw 'IPv4 DNS was not applied'}\n");
            if (ipv6) sb.Append("if($c -notcontains '::1'){throw 'IPv6 DNS was not applied'}\n");
        }
        sb.Append("Clear-DnsClientCache\n}catch{Write-Output $_.Exception.Message;exit 1}\n");
        return sb.ToString();
    }

    /// <summary>Per adapter, only if it still points at us: reset to automatic, then re-apply the recorded static servers per family.</summary>
    public static string Restore(Journal j)
    {
        var sb = new StringBuilder("$ProgressPreference='SilentlyContinue'\n$bad=0\n");
        foreach (var a in j.Adapters)
        {
            if (!System.Guid.TryParse(a.Guid, out var g)) continue;
            var ips = a.Servers.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).OfType<IPAddress>().ToList();
            var v4 = ips.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(i => $"'{i}'").ToList();
            var v6 = ips.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).Select(i => $"'{i}'").ToList();
            var restore = new StringBuilder("Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses\n");
            if (a.Static)
            {
                if (v4.Count > 0) restore.Append($"Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ({string.Join(",", v4)})\n");
                if (v6.Count > 0) restore.Append($"Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ({string.Join(",", v6)})\n");
            }
            sb.Append("try{").Append(Find(g.ToString("B"), a.Name));
            sb.Append("if($a){$c=@((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex).ServerAddresses)\n");
            sb.Append("if($c -contains '127.0.0.1' -or $c -contains '::1'){\n").Append(restore).Append("}}}catch{$bad=1;Write-Output $_.Exception.Message}\n");
        }
        sb.Append("try{Clear-DnsClientCache}catch{}\nexit $bad\n");
        return sb.ToString();
    }
}
