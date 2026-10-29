using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Hushwire.Core;

namespace Hushwire.App;

/// <summary>
/// Points adapter DNS at the local proxy and puts it back. The journal is written BEFORE any change, restore only touches
/// adapters that still point at us, and nothing else (firewall, proxies, hosts, VPN adapters) is modified.
/// </summary>
public sealed class NetConfig
{
    public bool HasJournal => File.Exists(Paths.JournalFile);
    public IReadOnlyList<IPAddress> Upstreams { get; private set; } = [];
    /// <summary>How the last DNS change was made: shown in diagnostics, so a slow fallback is visible.</summary>
    public string LastMethod { get; private set; } = "none yet";

    public static IEnumerable<NetworkInterface> Eligible()
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch (NetworkInformationException) { return []; }
        return all.Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2
            && !IsVirtual(n)
            && n.GetIPProperties().UnicastAddresses.Any(u => !u.Address.IsIPv6LinkLocal && !IPAddress.IsLoopback(u.Address)));
    }

    public static bool IsVirtual(NetworkInterface n)
    {
        string t = n.Description + " " + n.Name;
        foreach (var w in new[] { "Virtual", "Hyper-V", "VMware", "VirtualBox", "TAP-", "WireGuard", "Tunnel", "VPN", "Loopback", "vEthernet", "WSL", "Bluetooth", "TUN", "Filter", "WFP", "QoS", "Packet Scheduler", "Native MAC", "Npcap" })
            if (t.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool IsStaticDns(string guid)
    {
        foreach (var key in new[] { "Tcpip", "Tcpip6" })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{key}\Parameters\Interfaces\{guid}");
            if (k?.GetValue("NameServer") is string s && s.Trim().Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Null means there is no usable journal (missing or corrupt). A file that is merely locked right now (antivirus,
    /// backup) is retried and then reported as an error: treating it as "no journal" would lose the original DNS settings.
    /// </summary>
    Journal? Load()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return JsonSerializer.Deserialize<Journal>(File.ReadAllText(Paths.JournalFile)); }
            catch (JsonException) { return null; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5) { System.Threading.Thread.Sleep(200); }
        }
    }

    void Write(Journal j)
    {
        Paths.Ensure();
        var tmp = Paths.JournalFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(j));
        File.Move(tmp, Paths.JournalFile, true);
    }

    /// <summary>Records current DNS for adapters not yet in the journal. Returns the adapters that need to be pointed at us.</summary>
    public List<AdapterState> Capture()
    {
        var j = (HasJournal ? Load() : null) ?? new Journal();
        var fresh = new List<AdapterState>();
        foreach (var n in Eligible())
        {
            if (!Guid.TryParse(n.Id, out var g)) continue;
            var id = g.ToString("B");
            if (j.Adapters.Any(a => a.Guid.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            var servers = n.GetIPProperties().DnsAddresses.Where(a => !IPAddress.IsLoopback(a)).Select(a => a.ToString()).ToArray();
            var st = new AdapterState(id, n.Name, IsStaticDns(id), servers);
            j.Adapters.Add(st); fresh.Add(st);
        }
        if (fresh.Count > 0) Write(j);
        Upstreams = j.Adapters.SelectMany(a => a.Servers).Distinct()
            .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).OfType<IPAddress>().ToList();
        return fresh;
    }

    public async Task ApplyAsync(IEnumerable<AdapterState> adapters, bool ipv6)
    {
        var list = adapters.ToList();
        if (NativeDns.TryApply(list, ipv6)) { LastMethod = "Windows API"; return; }   // milliseconds; anything unexpected falls through to PowerShell
        LastMethod = "PowerShell (the direct API was not accepted)";
        var (code, output) = await Ps.RunAsync(NetScripts.Apply(list, ipv6)).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException("Could not change DNS settings: " + output.Trim());
    }

    /// <summary>Puts every journaled adapter back, but only if it still points at the proxy. Deletes the journal on success.</summary>
    public async Task RestoreAsync()
    {
        if (!HasJournal) return;
        var j = Load();
        if (j is null) { if (File.Exists(Paths.JournalFile)) File.Move(Paths.JournalFile, Paths.JournalFile + ".corrupt", true); return; }
        if (NativeDns.TryRestore(j)) LastMethod = "Windows API";
        else
        {
            LastMethod = "PowerShell (the direct API was not accepted)";
            var (code, output) = await Ps.RunAsync(NetScripts.Restore(j)).ConfigureAwait(false);
            if (code != 0) throw new InvalidOperationException("Could not restore DNS settings: " + output.Trim());
        }
        File.Delete(Paths.JournalFile);
        Upstreams = [];
    }
}

public static class Ps
{
    public static async Task<(int Code, string Output)> RunAsync(string script)
    {
        script = "$ProgressPreference='SilentlyContinue'\n" + script;
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().ConfigureAwait(false);
        string err = await e.ConfigureAwait(false);
        // PowerShell can wrap stderr as CLIXML; keep only the readable error text.
        if (err.StartsWith("#< CLIXML"))
        {
            var msgs = Regex.Matches(err, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline).Select(m => m.Groups[1].Value);
            err = string.Join("\n", msgs).Replace("_x000D__x000A_", "\n");
        }
        return (p.ExitCode, ((await o.ConfigureAwait(false)) + "\n" + err).Trim());
    }

    static string Q(string s) => s.Replace("'", "''");

    /// <summary>
    /// Scheduled tasks run Hushwire elevated / as SYSTEM, so they must not point at a folder a normal user can write to
    /// (anything there could be swapped for malware that then runs with admin rights). Single-file builds are copied to
    /// %ProgramFiles%\Hushwire, which only administrators can modify, and the tasks use that copy.
    /// </summary>
    /// <summary>Best-effort removal of the previous exe left beside the installed one after an in-place swap (can be 150+ MB).</summary>
    public static void CleanInstalledLeftovers()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hushwire");
            foreach (var f in new[] { "Hushwire.exe.old", "Hushwire.exe.new" })
                if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still in use or not allowed: next start */ }
    }

    public static string TrustedExePath()
    {
        string src = Environment.ProcessPath!;
        if (File.Exists(Path.Combine(Path.GetDirectoryName(src)!, "Hushwire.dll"))) return src; // dev/framework-dependent build: not self-contained
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hushwire");
        string dst = Path.Combine(dir, "Hushwire.exe");
        if (string.Equals(Path.GetFullPath(src), dst, StringComparison.OrdinalIgnoreCase)) return dst;
        Directory.CreateDirectory(dir);
        var s = new FileInfo(src);
        if (!File.Exists(dst) || new FileInfo(dst).Length != s.Length || File.GetLastWriteTimeUtc(dst) < s.LastWriteTimeUtc)
        {
            string tmp = dst + ".new", old = dst + ".old";
            File.Copy(src, tmp, true);
            try { File.Delete(old); } catch (IOException) { }
            if (File.Exists(dst)) File.Move(dst, old, true); // a running copy can be renamed, not overwritten
            File.Move(tmp, dst, true);
        }
        return dst;
    }

    public static Task<(int, string)> SetTask(string name, bool on, string mode)
    {
        if (!on) return RunAsync($"Unregister-ScheduledTask -TaskName '{name}' -Confirm:$false -ErrorAction SilentlyContinue");
        string exe = Q(TrustedExePath());
        // mode "boot": run "--restore" as SYSTEM at startup (safety net if the machine died with DNS still pointed at us)
        // mode "logon": start the tray app at sign-in, elevated, without a UAC prompt
        string script = mode == "boot"
            ? $"$a=New-ScheduledTaskAction -Execute '{exe}' -Argument '--restore';$t=New-ScheduledTaskTrigger -AtStartup;$p=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest -LogonType ServiceAccount;"
            : $"$a=New-ScheduledTaskAction -Execute '{exe}' -Argument '--tray';$t=New-ScheduledTaskTrigger -AtLogOn -User \"$env:USERDOMAIN\\$env:USERNAME\";$p=New-ScheduledTaskPrincipal -UserId \"$env:USERDOMAIN\\$env:USERNAME\" -RunLevel Highest -LogonType Interactive;";
        script += $"$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero);Register-ScheduledTask -TaskName '{name}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";
        return RunAsync(script).ContinueWith(t => (t.Result.Code, t.Result.Output));
    }
}
