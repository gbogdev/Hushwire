using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Hushwire.App;

/// <param name="Direct">false when the sender is the Windows DNS Client service, i.e. the real app is unknown.</param>
public sealed record AppInfo(int Pid, string Path, string Name, bool Direct);

/// <summary>
/// Best-effort "which program asked". Windows apps normally ask the DNS Client service (svchost), which then queries us,
/// so those show as the DNS Client. Apps with their own resolver (browsers, many games) are identified by their socket.
/// </summary>
public static class Attribution
{
    static readonly ConcurrentDictionary<int, (AppInfo? App, long At)> pids = new();
    static readonly ConcurrentDictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    public static AppInfo? Resolve(IPEndPoint remote, bool tcp)
    {
        if (!IPAddress.IsLoopback(remote.Address)) return null;
        int pid = OwnerTables.FindPid(tcp, remote.AddressFamily, remote.Port);
        if (pid <= 0) return null;
        long now = Environment.TickCount64;
        if (pids.TryGetValue(pid, out var c) && now - c.At < 15_000) return c.App;
        var app = Describe(pid);
        if (pids.Count > 2000) pids.Clear();
        pids[pid] = (app, now);
        return app;
    }

    static AppInfo? Describe(int pid)
    {
        var path = ImagePath(pid);
        if (path is null) return new(pid, "", $"PID {pid}", false);
        var file = System.IO.Path.GetFileName(path);
        if (file.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            return new(pid, path, "Windows DNS Client (shared service)", false);
        var name = names.GetOrAdd(path, p =>
        {
            try { var d = FileVersionInfo.GetVersionInfo(p).FileDescription; if (!string.IsNullOrWhiteSpace(d)) return d.Trim(); } catch { }
            return System.IO.Path.GetFileNameWithoutExtension(p);
        });
        return new(pid, path, name, true);
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

    /// <summary>Display name of the program whose window is in front, or null (none, or it is Hushwire itself).</summary>
    public static string? ForegroundName()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out int pid);
        if (pid <= 0 || pid == Environment.ProcessId) return null;
        try { return Describe(pid)?.Name; } catch { return null; }
    }

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}

/// <summary>Port → owning PID via the IP Helper tables (cached ~200 ms).</summary>
public static class OwnerTables
{
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int cls, uint reserved);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int cls, uint reserved);

    /// <summary>Immutable once built, so readers never lock.</summary>
    sealed class Snap { public readonly Dictionary<int, int> Map; public readonly long At; public Snap(Dictionary<int, int> m, long at) { Map = m; At = at; } }
    static readonly Snap[] snaps = [new(new(), 0), new(new(), 0), new(new(), 0), new(new(), 0)];
    static readonly object gate = new();

    const int FreshMs = 200;      // a snapshot younger than this is always trusted
    const int MissRetryMs = 50;   // unknown port: re-read the table, but at most this often

    static int Index(bool tcp, AddressFamily f) => (tcp ? 2 : 0) + (f == AddressFamily.InterNetworkV6 ? 1 : 0);

    public static int FindPid(bool tcp, AddressFamily f, int port)
    {
        int i = Index(tcp, f);
        var s = Volatile.Read(ref snaps[i]);
        long now = Environment.TickCount64;
        if (now - s.At > FreshMs || (!s.Map.ContainsKey(port) && now - s.At > MissRetryMs))
        {
            lock (gate)   // only one thread re-reads the table; the rest reuse its result
            {
                s = snaps[i];
                if (Environment.TickCount64 - s.At > MissRetryMs)
                {
                    s = new Snap(Read(tcp, f, listenOnly: false), Environment.TickCount64);
                    Volatile.Write(ref snaps[i], s);
                }
            }
        }
        return s.Map.TryGetValue(port, out var pid) ? pid : 0;
    }

    /// <summary>PID listening on a local port (used to name whoever already owns port 53).</summary>
    public static int FindListener(bool tcp, int port) => Read(tcp, AddressFamily.InterNetwork, listenOnly: true).TryGetValue(port, out var p) ? p : 0;

    static Dictionary<int, int> Read(bool tcp, AddressFamily f, bool listenOnly)
    {
        var map = new Dictionary<int, int>();
        bool v6 = f == AddressFamily.InterNetworkV6;
        int af = v6 ? 23 : 2, size = 0;
        IntPtr buf = IntPtr.Zero;
        try
        {
            bool ok = false;
            for (int i = 0; i < 4 && !ok; i++)
            {
                uint r = tcp ? GetExtendedTcpTable(buf, ref size, false, af, listenOnly ? 4 : 5, 0) : GetExtendedUdpTable(buf, ref size, false, af, 1, 0);
                if (r == 0) { ok = true; break; }
                if (r != 122) return map;                      // anything but "buffer too small": give up
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);              // table grew between calls: retry with the new size
            }
            if (!ok) return map;
            int n = Marshal.ReadInt32(buf);
            int row = tcp ? (v6 ? 56 : 24) : (v6 ? 28 : 12);
            int portOff = tcp ? (v6 ? 20 : 8) : (v6 ? 20 : 4);
            int pidOff = tcp ? (v6 ? 52 : 20) : (v6 ? 24 : 8);
            for (int i = 0; i < n; i++)
            {
                int b = 4 + i * row;
                int raw = Marshal.ReadInt32(buf, b + portOff);
                map[((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF)] = Marshal.ReadInt32(buf, b + pidOff);
            }
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        return map;
    }
}
