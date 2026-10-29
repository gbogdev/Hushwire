using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Hushwire.Core;

namespace Hushwire.App;

public static class Paths
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("HUSHWIRE_DATA") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hushwire");
    public static string ListsDir => Path.Combine(Dir, "lists");
    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string StatsFile => Path.Combine(Dir, "stats.json");
    public static string JournalFile => Path.Combine(Dir, "netstate.json");

    static bool ensured;

    /// <summary>
    /// Creates the data folder, and on a real install makes it writable only by administrators and SYSTEM. The boot-time
    /// recovery task runs as SYSTEM and acts on netstate.json, so a standard user must not be able to edit it (or to have
    /// created the folder first and own it). Developer runs (HUSHWIRE_DATA) keep default permissions.
    /// </summary>
    public static void Ensure()
    {
        Directory.CreateDirectory(Dir);
        if (ensured || Environment.GetEnvironmentVariable("HUSHWIRE_DATA") is { Length: > 0 }) return;
        ensured = true;
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var (sid, rights) in new[]
            {
                (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                (WellKnownSidType.AuthenticatedUserSid, FileSystemRights.ReadAndExecute),
            })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Dir).SetAccessControl(sec);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException) { /* not elevated: leave as is */ }
    }
}

public sealed class Settings
{
    public bool ProtectionWanted { get; set; }
    public string BlockMode { get; set; } = "NullIp";
    public string UpstreamOverride { get; set; } = "";
    public bool LogAllowed { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CnameCheck { get; set; } = true;
    /// <summary>Opt-in: filter YouTube/Twitch ads inside the page (needs the local certificate).</summary>
    public bool AdFilter { get; set; }
    public List<ListSub> Lists { get; set; } = [];
    public List<string> UserAllow { get; set; } = [];
    public List<string> UserBlock { get; set; } = [];
    public List<string> ExemptApps { get; set; } = [];

    public BlockMode Mode => BlockMode == "Nxdomain" ? Core.BlockMode.Nxdomain : Core.BlockMode.NullIp;

    public static List<ListSub> DefaultLists() => ListCatalog.All();
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Opt) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            if (e is JsonException) KeepCopy(Paths.SettingsFile);
            s = new();
        }
        Sanitize(s);
        return s;
    }

    /// <summary>A file we cannot read is replaced by defaults on the next save: keep the original next to it so nothing is lost for good.</summary>
    public static void KeepCopy(string file)
    {
        try { File.Copy(file, file + ".bad", true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Never trust the file: drop anything malformed rather than failing to start.</summary>
    static void Sanitize(Settings s)
    {
        s.Lists ??= []; s.UserAllow ??= []; s.UserBlock ??= []; s.ExemptApps ??= [];
        s.BlockMode ??= "NullIp"; s.UpstreamOverride ??= "";
        s.Lists.RemoveAll(l => l is null);
        foreach (var l in s.Lists) { l.Id ??= ""; l.Name ??= ""; l.Url ??= ""; l.Category ??= "Custom"; }
        foreach (var d in Settings.DefaultLists())
        {
            var have = s.Lists.FirstOrDefault(l => l.Id == d.Id);
            if (have is null) { s.Lists.Add(d); continue; }
            // keep the user's choices (enabled, interval, update history) but let the catalog fix names, URLs and notes
            have.Name = d.Name; have.Url = d.Url; have.Category = d.Category; have.License = d.License; have.Description = d.Description; have.Builtin = true;
        }
        s.Lists = s.Lists.Where(l => ListManager.IsValidId(l.Id) && ListManager.IsHttps(l.Url)).ToList();
        foreach (var l in s.Lists) { l.IntervalHours = Math.Clamp(l.IntervalHours, 1, 720); if (l.Name.Length > 80) l.Name = l.Name[..80]; }
        s.UserAllow = s.UserAllow.Select(Domain.Normalize).OfType<string>().Distinct().ToList();
        s.UserBlock = s.UserBlock.Select(Domain.Normalize).OfType<string>().Distinct().ToList();
        s.ExemptApps = s.ExemptApps.Where(p => p.Length < 520).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (s.BlockMode is not ("NullIp" or "Nxdomain")) s.BlockMode = "NullIp";
    }

    static readonly object saveGate = new();

    /// <summary>Saves from any thread (UI clicks, list updates and timers all call this); writes are serialized.</summary>
    public static void Save(Settings s)
    {
        lock (saveGate)
        {
            Paths.Ensure();
            string json;
            try { json = JsonSerializer.Serialize(s, Opt); }
            catch (InvalidOperationException) { json = JsonSerializer.Serialize(s, Opt); } // a list was edited mid-serialize: once more
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.SettingsFile, true);
        }
    }
}
