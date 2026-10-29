using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Hushwire.Core;

namespace Hushwire.App;

/// <summary>
/// Reads and sets adapter DNS servers through the Windows IP Helper API (Windows 10 2004 and later), in milliseconds.
/// The PowerShell route takes several seconds per change (process start plus two module loads) and is kept only as the
/// fallback: every change here is read back and verified, and anything unexpected returns false so the caller falls back.
/// </summary>
public static class NativeDns
{
    const uint NameServerFlag = 0x2, Ipv6Flag = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain, NameServer, SearchList;
        public uint RegistrationEnabled, RegisterAdapterName, EnableLlmnr, QueryAdapterName;
        public IntPtr ProfileNameServer;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)] static extern uint SetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] static extern uint GetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] static extern void FreeInterfaceDnsSettings(ref DnsInterfaceSettings settings);

    /// <summary>The servers configured on the adapter for one IP family; empty = automatic (DHCP). Null if it cannot be read.</summary>
    public static string[]? Read(Guid adapter, bool ipv6)
    {
        try
        {
            var s = new DnsInterfaceSettings { Version = 1, Flags = NameServerFlag | (ipv6 ? Ipv6Flag : 0) };
            if (GetInterfaceDnsSettings(adapter, ref s) != 0) return null;
            try
            {
                var text = Marshal.PtrToStringUni(s.NameServer) ?? "";
                return text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            }
            finally { FreeInterfaceDnsSettings(ref s); }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>Sets the servers for one family ("" = back to automatic). Returns whether Windows accepted it.</summary>
    static bool Write(Guid adapter, bool ipv6, string servers)
    {
        var name = Marshal.StringToHGlobalUni(servers);
        try
        {
            var s = new DnsInterfaceSettings { Version = 1, Flags = NameServerFlag | (ipv6 ? Ipv6Flag : 0), NameServer = name };
            return SetInterfaceDnsSettings(adapter, ref s) == 0;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(name); }
    }

    static bool Is(string[]? have, params string[] want) =>
        have is not null && have.Length == want.Length && have.Zip(want).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    static bool PointsAtUs(string[]? v4, string[]? v6) =>
        (v4?.Any(x => x is "127.0.0.1") ?? false) || (v6?.Any(x => x is "::1") ?? false);

    /// <summary>A second, independent view (the classic adapter list) of whether the adapter's DNS is us.</summary>
    static bool LoopbackPerAdapterList(Guid adapter) =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => Guid.TryParse(n.Id, out var g) && g == adapter
            && n.GetIPProperties().DnsAddresses.Any(System.Net.IPAddress.IsLoopback));

    static bool Exists(Guid adapter) =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => Guid.TryParse(n.Id, out var g) && g == adapter);

    /// <summary>Points each adapter at the local proxy and checks it took. False = nothing can be assumed; use the fallback.</summary>
    public static bool TryApply(IEnumerable<AdapterState> adapters, bool ipv6)
    {
        foreach (var a in adapters)
        {
            if (!Guid.TryParse(a.Guid, out var g)) return false;
            if (!Write(g, false, "127.0.0.1") || !Is(Read(g, false), "127.0.0.1")) return false;
            if (ipv6 && (!Write(g, true, "::1") || !Is(Read(g, true), "::1"))) return false;
        }
        DnsApi.Flush();
        return true;
    }

    /// <summary>
    /// Puts the recorded servers back on every adapter that still points at us; adapters someone else has changed are left alone.
    /// Returns false (and the caller falls back) if anything could not be read, written or verified.
    /// </summary>
    public static bool TryRestore(Journal j)
    {
        foreach (var a in j.Adapters)
        {
            if (!Guid.TryParse(a.Guid, out var g)) continue;
            var v4 = Read(g, false); var v6 = Read(g, true);
            if (v4 is null && v6 is null)
            {
                if (Exists(g)) return false;     // present but unreadable: not safe to assume it's fine
                continue;                        // adapter is gone (unplugged): nothing to restore
            }
            // Never skip an adapter on one reading alone: leaving DNS pointed at a program that has exited breaks the internet.
            if (!PointsAtUs(v4, v6) && !LoopbackPerAdapterList(g)) continue;

            var want4 = a.Static ? a.Servers.Where(IsV4).ToArray() : [];
            var want6 = a.Static ? a.Servers.Where(x => !IsV4(x)).ToArray() : [];
            if (!Write(g, false, string.Join(",", want4)) || !Write(g, true, string.Join(",", want6))) return false;
            if (!Is(Read(g, false), want4) || !Is(Read(g, true), want6) || LoopbackPerAdapterList(g)) return false;
        }
        DnsApi.Flush();
        return true;
    }

    static bool IsV4(string s) => System.Net.IPAddress.TryParse(s, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}
