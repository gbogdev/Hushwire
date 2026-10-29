using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Hushwire.App;

/// <summary>
/// What the installer needs from the app: ask a running copy to quit cleanly, and remove everything Hushwire put on the
/// machine. Both run headless (no window) via <c>--stop</c> and <c>--uninstall</c>.
/// </summary>
public static class Lifecycle
{
    /// <summary>Named objects are machine-wide in normal use so the installer (running as SYSTEM) can reach them; dev mode stays per-session.</summary>
    public static string Name(string what, bool dev) => (dev ? @"Local\" : @"Global\") + "Hushwire." + what + (dev ? ".Dev" : "");

    public static EventWaitHandle CreateQuitEvent(bool dev) => new(false, EventResetMode.AutoReset, Name("Quit", dev));

    /// <summary>Asks a running Hushwire to exit (which restores DNS first) and waits for it. 0 = not running or exited, 1 = still running.</summary>
    public static int Stop(bool dev, TimeSpan wait)
    {
        if (!EventWaitHandleAcl.TryOpenExisting(Name("Quit", dev), System.Security.AccessControl.EventWaitHandleRights.Modify, out var quit)) return 0;
        using (quit) quit.Set();
        // the running copy holds this mutex until the process ends
        if (!MutexAcl.TryOpenExisting(Name("App", dev), System.Security.AccessControl.MutexRights.Synchronize, out var held)) return 0;
        using (held)
        {
            try { return held.WaitOne(wait) ? 0 : 1; }
            catch (AbandonedMutexException) { return 0; }      // the owner ended: exactly what we wanted
        }
    }

    /// <summary>
    /// Undoes everything Hushwire does to the machine: DNS back to normal, scheduled tasks, the YouTube/Twitch certificate and
    /// keys, and Hushwire's own data. Never deletes anything outside Hushwire's known files. Returns 0 on full success.
    /// </summary>
    public static int Uninstall(bool dev = false)
    {
        var problems = new List<string>();
        void Try(string what, Action a) { try { a(); } catch (Exception e) { problems.Add($"{what}: {e.Message}"); } }

        Try("restore DNS", () => new NetConfig().RestoreAsync().GetAwaiter().GetResult());
        // developer runs (--dev) never touch the real scheduled tasks
        foreach (var task in dev ? [] : new[] { "HushwireRecover", "HushwireApp" })
            Try("remove task " + task, () => RunQuietly("schtasks.exe", $"/Delete /TN {task} /F"));
        Try("remove certificate", () => new AdFilterService().RemoveCertificate());
        Try("delete data", DeleteData);

        if (problems.Count > 0)
            try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "hushwire-uninstall.log"), problems); } catch { }
        return problems.Count == 0 ? 0 : 1;
    }

    static void DeleteData()
    {
        string dir = Paths.Dir;
        if (!Directory.Exists(dir)) return;
        foreach (var sub in new[] { "lists", "ca" })
            if (Directory.Exists(Path.Combine(dir, sub))) Directory.Delete(Path.Combine(dir, sub), true);
        foreach (var f in new[] { "settings.json", "stats.json", "netstate.json", "netstate.json.tmp", "netstate.json.corrupt", "settings.json.tmp", "settings.json.bad", "stats.json.tmp", "stats.json.bad", "error.log" })
            File.Delete(Path.Combine(dir, f));
        if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);   // anything unexpected stays: it isn't ours to delete
    }

    static void RunQuietly(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(15000);               // "task not found" exits non-zero; that just means there was nothing to remove
    }
}
