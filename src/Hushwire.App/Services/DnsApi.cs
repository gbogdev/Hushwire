using System;
using System.Runtime.InteropServices;

namespace Hushwire.App;

/// <summary>Windows' own DNS resolver cache.</summary>
public static class DnsApi
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DnsFlushResolverCache();

    /// <summary>
    /// Empties the cache so newly blocked/unblocked names take effect now. A direct call: a PowerShell process costs
    /// about half a second of CPU and ~40 MB each time. Falls back to PowerShell if the export is unavailable.
    /// </summary>
    public static void Flush()
    {
        try { if (DnsFlushResolverCache()) return; }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        _ = Ps.RunAsync("Clear-DnsClientCache");
    }
}

/// <summary>Hands memory back to Windows after the window has been hidden for a few seconds.</summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    public static void Run()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);   // trim pages not touched recently
    }
}
