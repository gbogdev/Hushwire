using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hushwire.Core;

namespace Hushwire.App;

public enum ProtectionState { Off, Starting, On, Paused, Error }

public sealed record ActivityRow(DateTime Time, string Domain, string Type, string Result, string Source, string App)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Owns the proxy, the filter snapshot, list updates and the DNS takeover. One instance per process.</summary>
public sealed partial class Protection : IDisposable
{
    public Settings S { get; }
    public Stats Stats { get; }
    public ListManager Lists { get; }
    public NetConfig Net { get; } = new();
    public bool Dev { get; }
    public int Port => Dev ? 5300 : 53;

    public ProtectionState State { get; private set; } = ProtectionState.Off;
    public string Detail { get; private set; } = "";
    public DateTime? PausedUntil { get; private set; }
    public string ListStatus { get; private set; } = "";
    public FilterSnapshot Snapshot => snapshot;
    public DnsProxy? Proxy => proxy;
    public event Action? Changed;

    volatile FilterSnapshot snapshot = FilterSnapshot.Empty;
    DnsProxy? proxy;
    volatile bool paused;
    HashSet<string> exempt = new(StringComparer.OrdinalIgnoreCase);
    readonly Channel<(QueryEvent E, AppInfo? App)> channel = Channel.CreateBounded<(QueryEvent, AppInfo?)>(new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly List<ActivityRow> activity = [];
    readonly object actGate = new();
    int activityVersion;
    readonly SemaphoreSlim opGate = new(1, 1);
    readonly SemaphoreSlim listGate = new(1, 1);
    Timer? pauseTimer, updateTimer, saveTimer;
    Process? watchdog;
    readonly AdFilterService adFilter = new();
    public string AdFilterStatus => Dev ? "Not available in developer mode" : !S.AdFilter ? "Off" : adFilter.Status;
    public bool AdFilterRunning => adFilter.Running;
    IReadOnlyList<IPAddress> devUpstreams = [];
    readonly CancellationTokenSource cts = new();

    public Protection(Settings s, bool dev)
    {
        S = s; Dev = dev;
        Stats = Stats.Load();
        Lists = new ListManager(Paths.ListsDir);
        RefreshExempt();
    }

    void Raise() => Changed?.Invoke();
    void Set(ProtectionState st, string detail = "") { State = st; Detail = detail; Raise(); }
    public void Save() => SettingsStore.Save(S);
    public void RefreshExempt() => exempt = new(S.ExemptApps, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- startup

    public async Task InitAsync()
    {
        _ = Task.Run(ConsumeAsync);
        if (!Dev) _ = Task.Run(Ps.CleanInstalledLeftovers);
        saveTimer = new Timer(_ => Stats.Save(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        StartHousekeeping();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        if (!Dev && Net.HasJournal)
        {
            // Previous run ended without restoring: put the network back before doing anything else.
            try { await Net.RestoreAsync(); } catch (Exception e) { Detail = "Recovery: " + e.Message; }
        }
        await RebuildAsync();
        ScheduleUpdates();
        if (S.ProtectionWanted) await EnableAsync();
    }

    // ---------------------------------------------------------------- filter lists

    public async Task RebuildAsync()
    {
        var enabled = S.Lists.Where(l => l.Enabled).ToList();
        var allow = EffectiveAllow(); var block = S.UserBlock.ToList();
        listInfo = S.Lists.ToDictionary(l => l.Id, l => (l.Name, l.Category));
        var snap = await Task.Run(() =>
        {
            // size the tables from the files up front (≈20 bytes per rule) instead of growing them repeatedly
            var b = new FilterBuilder((int)Math.Min(int.MaxValue / 2, Lists.StoredBytes(enabled.Select(l => l.Id)) / 20));
            foreach (var l in enabled)
            {
                var bytes = Lists.ReadBytes(l.Id);
                if (bytes is null && l.LastSuccessUtc is not null) l.LastError = "Could not read the saved list";
                b.AddListUtf8(l.Id, bytes ?? []);   // always registered, so list indexes stay stable
            }
            return b.Build(allow, block);
        });
        snapshot = snap;
        proxy?.Cache.Clear();
        ListStatus = $"{snap.RuleCount:N0} rules from {enabled.Count} lists";
        // Loading lists borrows tens of MB of large buffers; hand them back now instead of sitting on them.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Raise();
    }

    /// <summary>Applies changed allow/block rules instantly: the parsed lists are reused, nothing is re-read.</summary>
    public async Task ApplyRulesAsync()
    {
        Save();
        snapshot = snapshot.WithUserRules(EffectiveAllow(), S.UserBlock.ToList());
        proxy?.Cache.Clear();
        if (!Dev && proxy is not null) DnsApi.Flush();
        Raise();
        await Task.CompletedTask;
    }

    /// <summary>Downloads due lists (all enabled when <paramref name="force"/>). Failed lists keep their previous good copy.</summary>
    public async Task UpdateListsAsync(bool force, string? onlyId = null)
    {
        await listGate.WaitAsync();
        try
        {
            bool changed = false;
            var due = S.Lists.Where(l => (l.Enabled || onlyId == l.Id) && (onlyId is null || onlyId == l.Id)
                    && (force || l.LastSuccessUtc is null || DateTime.UtcNow - l.LastSuccessUtc.Value >= TimeSpan.FromHours(l.IntervalHours))).ToList();
            if (due.Count > 0) { ListStatus = due.Count == 1 ? $"Updating {due[0].Name}…" : $"Updating {due.Count} lists…"; Raise(); }
            using var slots = new SemaphoreSlim(4);   // a few downloads at a time: much faster than one by one, still polite
            await Task.WhenAll(due.Select(async l =>
            {
                await slots.WaitAsync(cts.Token);
                try
                {
                    l.LastAttemptUtc = DateTime.UtcNow;
                    var r = await Lists.UpdateAsync(l, cts.Token);
                    if (r.Ok)
                    {
                        l.LastSuccessUtc = DateTime.UtcNow; l.LastError = null; l.ETag = r.ETag ?? l.ETag;
                        if (!r.NotModified) { l.RuleCount = r.Rules; l.UnsupportedCount = r.Unsupported; changed = true; }
                    }
                    else l.LastError = r.Error;
                }
                finally { slots.Release(); }
            }));
            Save();
            var failedLists = due.Where(l => l.LastError is not null).ToList();
            if (failedLists.Count > 0)
                Notify("lists", "Filter list update failed", failedLists.Count == 1 ? $"{failedLists[0].Name} could not be updated. The previous copy is still in use." : $"{failedLists.Count} lists could not be updated. The previous copies are still in use.");
            if (changed || snapshot.RuleCount == 0) await RebuildAsync();
            else { ListStatus = $"{snapshot.RuleCount:N0} rules from {S.Lists.Count(l => l.Enabled)} lists"; Raise(); }
            if (changed && proxy is not null && !Dev) DnsApi.Flush();
        }
        finally { listGate.Release(); ScheduleUpdates(); }
    }

    void ScheduleUpdates()
    {
        var next = S.Lists.Where(l => l.Enabled)
            .Select(l => (l.LastSuccessUtc ?? DateTime.MinValue).AddHours(l.IntervalHours)).DefaultIfEmpty(DateTime.UtcNow.AddHours(6)).Min();
        var wait = next - DateTime.UtcNow;
        if (wait < TimeSpan.FromMinutes(2)) wait = TimeSpan.FromMinutes(2);
        if (wait > TimeSpan.FromHours(6)) wait = TimeSpan.FromHours(6);
        updateTimer?.Dispose();
        updateTimer = new Timer(async _ => { try { await UpdateListsAsync(false); } catch { } }, null, wait, Timeout.InfiniteTimeSpan);
    }

    // ---------------------------------------------------------------- enable / disable / pause

    public IReadOnlyList<IPAddress> ResolveUpstreams()
    {
        var o = S.UpstreamOverride.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => IPAddress.TryParse(x, out var ip) ? ip : null).OfType<IPAddress>().Where(ip => !IPAddress.IsLoopback(ip)).ToList();
        if (o.Count > 0) return o;
        return (Dev ? devUpstreams : Net.Upstreams).Where(ip => !IPAddress.IsLoopback(ip)).ToList();
    }

    DnsProxy NewProxy() => new(Port)
    {
        Snapshot = () => snapshot,
        Active = () => !paused,
        Upstreams = ResolveUpstreams,
        Redirect = n => !paused && adFilter.Running && Hushwire.Core.AdFilter.InterceptList.Matches(n),
        RedirectIpv6 = () => adFilter.Ipv6,
        Mode = () => S.Mode,
        CnameCheck = () => S.CnameCheck,
        BlockSecureDns = () => S.BlockSecureDns,
        HasExemptApps = () => exempt.Count > 0,
        IsExemptApp = (r, tcp) => Attribution.Resolve(r, tcp) is { Direct: true } a && exempt.Contains(a.Path),
        OnQuery = e => channel.Writer.TryWrite((e, SafeResolve(e))),
    };

    public async Task EnableAsync()
    {
        await opGate.WaitAsync();
        try
        {
            if (State is ProtectionState.On or ProtectionState.Paused) return;
            Set(ProtectionState.Starting, "Starting…");
            paused = false; PausedUntil = null;
            try
            {
                if (!Dev && Net.HasJournal) await Net.RestoreAsync();
                proxy?.Dispose(); proxy = null;     // left running by a failed Turn off; DNS is back to normal now, so replace it
                if (snapshot.RuleCount == 0 && S.Lists.Any(l => l.Enabled))
                {
                    Detail = "Downloading filter lists first…"; Raise();
                    await Task.WhenAny(UpdateListsAsync(false), Task.Delay(45_000));
                }
                devUpstreams = NetConfig.Eligible().SelectMany(n => n.GetIPProperties().DnsAddresses).Where(a => !IPAddress.IsLoopback(a)).Distinct().ToList();
                var p = NewProxy();
                try { p.Start(); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    p.Dispose();
                    int pid = OwnerTables.FindListener(false, Port);
                    var who = pid > 0 ? Attribution.ImagePath(pid) ?? $"PID {pid}" : "another program";
                    throw new InvalidOperationException($"Port {Port} is already in use by {who}. Close it (or stop Internet Connection Sharing) and try again.");
                }
                proxy = p;
                ApplyEncryptedUpstream();

                if (!Dev)
                {
                    var fresh = Net.Capture();
                    if (ResolveUpstreams().Count == 0)
                        throw new InvalidOperationException("No upstream DNS server could be detected. Enter one under Settings → Upstream DNS (for example your router's address).");
                    await Net.ApplyAsync(fresh, p.Ipv6Bound);
                    await Ps.SetTask("HushwireRecover", true, "boot");
                    StartWatchdog();
                    await StartAdFilterIfWantedAsync();
                }
                S.ProtectionWanted = true; Save();
                Set(ProtectionState.On, Dev ? $"Developer mode: listening on 127.0.0.1:{Port}, network settings untouched" : "");
            }
            catch (Exception e)
            {
                bool restored = true;
                if (!Dev) try { await Net.RestoreAsync(); } catch { restored = false; }
                if (restored) { proxy?.Dispose(); proxy = null; }   // otherwise keep answering DNS the adapters still point at
                Set(ProtectionState.Error, e.Message);
            }
        }
        finally { opGate.Release(); }
    }

    public async Task DisableAsync()
    {
        await opGate.WaitAsync();
        try
        {
            pauseTimer?.Dispose(); paused = false; PausedUntil = null;
            S.ProtectionWanted = false; Save();
            string err = "";
            if (!Dev)
            {
                try { await Net.RestoreAsync(); } catch (Exception e) { err = e.Message; }
                // If DNS could not be put back, keep the recovery task: it is what fixes it at the next boot.
                if (err.Length == 0) await Ps.SetTask("HushwireRecover", false, "");
            }
            adFilter.Stop();
            // The adapters may still point at the proxy: stopping it now would leave the PC with no working DNS.
            if (err.Length == 0) { proxy?.Dispose(); proxy = null; }
            Set(err.Length > 0 ? ProtectionState.Error : ProtectionState.Off,
                err.Length > 0 ? err + " Hushwire keeps running so your internet keeps working; try Turn off again, or use the \"repair network settings\" shortcut." : err);
        }
        finally { opGate.Release(); }
    }

    public void Pause(TimeSpan span)
    {
        if (State is not (ProtectionState.On or ProtectionState.Paused)) return;
        paused = true; PausedUntil = DateTime.Now + span;
        proxy?.Cache.Clear();
        if (!Dev) DnsApi.Flush();
        pauseTimer?.Dispose();
        pauseTimer = new Timer(_ => Resume(), null, span, Timeout.InfiniteTimeSpan);
        Set(ProtectionState.Paused);
    }

    public void Resume()
    {
        if (State != ProtectionState.Paused) return;
        if (pausedBySchedule) { pausedBySchedule = false; scheduleSuppressed = true; }     // you resumed during a scheduled pause: leave it on until that window ends
        pauseTimer?.Dispose(); paused = false; PausedUntil = null;
        proxy?.Cache.Clear();
        if (!Dev) DnsApi.Flush();
        Set(ProtectionState.On);
    }

    /// <summary>Called on exit: put DNS back synchronously. Protection is re-enabled on next start if it was on.</summary>
    public void ShutdownRestore()
    {
        lock (shutdownGate)
        {
            if (shutDown) return;      // the tray's Exit already did this on a background thread; OnExit calls it again as a safety net
            shutDown = true;
        }
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Stats.Save();
        adFilter.Stop();
        proxy?.Dispose(); proxy = null;
        // run on a pool thread so no UI synchronization context can be involved in the wait
        if (!Dev && Net.HasJournal) try { Task.Run(() => Net.RestoreAsync()).GetAwaiter().GetResult(); } catch { }
        try { if (watchdog is { HasExited: false }) watchdog.Kill(); } catch { }
    }

    readonly object shutdownGate = new();
    bool shutDown;
    public bool IsShutDown { get { lock (shutdownGate) return shutDown; } }

    void StartWatchdog()
    {
        if (watchdog is { HasExited: false }) return;
        try { watchdog = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true }); }
        catch { }
    }

    CancellationTokenSource? debounce;
    void OnNetworkChanged(object? s, EventArgs e)
    {
        if (Dev || State is not (ProtectionState.On or ProtectionState.Paused)) return;
        debounce?.Cancel(); debounce = new();
        var token = debounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, token);
                await opGate.WaitAsync(token);
                try
                {
                    var fresh = Net.Capture();
                    if (fresh.Count > 0 && proxy is not null) await Net.ApplyAsync(fresh, proxy.Ipv6Bound);
                    proxy?.Cache.Clear();
                }
                finally { opGate.Release(); }
                if (S.Lists.Any(l => l.Enabled && (l.LastSuccessUtc is null || DateTime.UtcNow - l.LastSuccessUtc > TimeSpan.FromHours(l.IntervalHours))))
                    await UpdateListsAsync(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Detail = ex.Message; Raise(); }
        });
    }

    // ---------------------------------------------------------------- YouTube / Twitch ad filter

    async Task StartAdFilterIfWantedAsync()
    {
        if (Dev || !S.AdFilter || proxy is null) return;
        var p = proxy;
        try
        {
            await adFilter.StartAsync((host, ct) => p.LookupAsync(host, ct), (site, n) => { Stats.RecordRewrite(site, n); });
            p.Cache.Clear();
            DnsApi.Flush();
        }
        catch (Exception e)
        {
            // The rest of Hushwire keeps working; only the in-page filter is off, and nothing is redirected to it.
            adFilter.Stop();
            S.AdFilter = false; Save();
            AdFilterError = e.Message;
        }
        Raise();
    }

    public string AdFilterError { get; private set; } = "";

    /// <summary>Turns the filter on (installing the local certificate on first use). Call only after the user has confirmed.</summary>
    public async Task EnableAdFilterAsync()
    {
        if (Dev) return;
        AdFilterError = ""; S.AdFilter = true; Save();
        if (proxy is not null) await StartAdFilterIfWantedAsync();
        Raise();
    }

    public void DisableAdFilter()
    {
        S.AdFilter = false; Save();
        adFilter.Stop(); AdFilterError = "";
        proxy?.Cache.Clear();
        if (!Dev) DnsApi.Flush();
        Raise();
    }

    /// <summary>Turns the filter off and removes its certificate and keys from this PC.</summary>
    public void RemoveAdFilterCertificate()
    {
        DisableAdFilter();
        try { adFilter.RemoveCertificate(); } catch (Exception e) { AdFilterError = "Could not remove the certificate: " + e.Message; }
        Raise();
    }

    public bool AdFilterCertificateInstalled => AdFilterService.CertificateInstalled();

    // ---------------------------------------------------------------- activity & stats

    static string TypeName(ushort t) => t switch { 1 => "A", 28 => "AAAA", 5 => "CNAME", 12 => "PTR", 15 => "MX", 16 => "TXT", 33 => "SRV", 65 => "HTTPS", 64 => "SVCB", 2 => "NS", _ => t.ToString() };

    volatile Dictionary<string, (string Name, string Category)> listInfo = new();   // refreshed whenever the lists change; read per query event

    /// <summary>Starts, switches or stops the encrypted upstream to match the setting. Safe to call at any time.</summary>
    public void ApplyEncryptedUpstream()
    {
        var p = proxy;
        if (p is null) return;
        var want = DohUpstream.Find(S.EncryptedDns);
        if (p.Doh?.Provider == want) return;
        var old = p.Doh;
        p.Doh = want is null ? null : new DohUpstream(want);
        old?.Dispose();
        p.Cache.Clear();
        Raise();
    }

    public string ListName(string? id) => id == "secure-dns" ? "Secure-DNS bypass protection" : id == "user" ? "Your blocklist" : id is not null && listInfo.TryGetValue(id, out var i) ? i.Name : id ?? "";
    string Category(string? id) => id == "secure-dns" ? "Secure DNS" : id == "user" ? "Your rules" : id is not null && listInfo.TryGetValue(id, out var i) ? i.Category : "Other";

    // Resolved on the query path: client sockets are often short-lived, so waiting would lose the owner.
    static AppInfo? SafeResolve(QueryEvent e) { try { return Attribution.Resolve(e.Remote, e.Tcp); } catch { return null; } }

    async Task ConsumeAsync()
    {
        await foreach (var (e, app) in channel.Reader.ReadAllAsync())
        {
            try
            {
                if (e.Remote.Port == 1 && IPAddress.IsLoopback(e.Remote.Address)) continue;      // Hushwire's own self-check lookups are not your traffic
                var d = e.Decision;
                bool cname = d.Origin == Origin.Cname;
                string source = d.Blocked ? (cname ? $"{ListName(d.ListId)} (via CNAME {d.Rule})" : ListName(d.ListId))
                    : d.Origin switch { Origin.UserAllow => "Your allowlist", Origin.ListAllow => "List exception", Origin.Protected => "Protected Windows domain", Origin.AppExempt => "App exempt", Origin.Redirect => "YouTube/Twitch ad filter", Origin.Paused => "Paused", _ => "" };
                Stats.Record(e.Name, d.Blocked, e.Cached, e.UpstreamFailed, cname, d.Blocked ? Category(d.ListId) : "", d.Blocked ? ListName(d.ListId) : "", app);

                if (d.Blocked || S.LogAllowed)
                {
                    string result = d.Blocked ? "Blocked" : e.UpstreamFailed ? "No answer" : e.Cached ? "Allowed (cache)" : "Allowed";
                    var row = new ActivityRow(e.Time, e.Name, TypeName(e.QType), result, source, app?.Name ?? "Unknown");
                    lock (actGate) { activity.Add(row); if (activity.Count > 3000) activity.RemoveRange(0, 500); activityVersion++; }
                }
            }
            catch { }
        }
    }

    /// <summary>Changes whenever the activity list does, so the UI can skip rebuilding it when nothing happened.</summary>
    public int ActivityVersion => Volatile.Read(ref activityVersion);

    public (int Version, List<ActivityRow> Rows) ActivitySnapshot(int max = 600)
    {
        lock (actGate) return (activityVersion, activity.AsEnumerable().Reverse().Take(max).ToList());
    }
    public void ClearActivity() { lock (actGate) { activity.Clear(); activityVersion++; } }

    // ---------------------------------------------------------------- rules & explain


    public string Explain(string input)
    {
        var n = Domain.Normalize(input);
        if (n is null) return "Not a valid domain name.";
        var d = snapshot.Decide(n);
        return d.Origin switch
        {
            Origin.UserAllow => $"Allowed by your allowlist entry “{d.Rule}”.",
            Origin.UserBlock => $"Blocked by your blocklist entry “{d.Rule}”.",
            Origin.Protected => $"Allowed: “{d.Rule}” is a protected Windows domain that lists cannot block.",
            Origin.ListAllow => $"Allowed by an exception rule in {ListName(d.ListId)} (“{d.Rule}”).",
            Origin.ListBlock => $"Blocked by {ListName(d.ListId)} (rule “{d.Rule}”).",
            _ => "Allowed: no rule matches this domain.",
        };
    }

    // ---------------------------------------------------------------- diagnostics

    public async Task<List<string>> DiagnoseAsync()
    {
        var r = new List<string>();
        void Ok(string s) => r.Add("✔  " + s);
        void Bad(string s) => r.Add("✖  " + s);
        void Info(string s) => r.Add("•  " + s);

        if (State is ProtectionState.On or ProtectionState.Paused) Ok(State == ProtectionState.On ? "Protection is on" : "Protection is paused"); else Bad($"Protection is {State.ToString().ToLower()}" + (Detail.Length > 0 ? $": {Detail}" : ""));
        if (proxy is not null) { Ok($"DNS proxy listening on 127.0.0.1:{Port}"); if (proxy.Ipv6Bound) Ok($"…and on [::1]:{Port}"); else Bad("IPv6 loopback listener unavailable (IPv6 DNS is not filtered)"); }
        if (!Dev)
            foreach (var n in NetConfig.Eligible())
            {
                var dns = n.GetIPProperties().DnsAddresses.ToList();
                bool ours = dns.Count > 0 && dns.All(IPAddress.IsLoopback);
                bool mixed = !ours && dns.Any(IPAddress.IsLoopback);
                string list = string.Join(", ", dns);
                if (State is ProtectionState.On or ProtectionState.Paused)
                {
                    if (ours) Ok($"{n.Name}: DNS → this app");
                    else if (mixed) Bad($"{n.Name}: partly protected ({list}); other DNS servers can bypass filtering");
                    else Bad($"{n.Name}: not using Hushwire ({list}). Another program or VPN may have changed it.");
                }
                else Info($"{n.Name}: DNS {list}");
            }
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && NetConfig.IsVirtual(n) && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            Info($"{n.Name}: virtual/VPN adapter — not modified; traffic using its DNS bypasses filtering");
        var ups = ResolveUpstreams();
        Info(ups.Count > 0 ? "Upstream DNS: " + string.Join(", ", ups) : "Upstream DNS: none detected");
        int enabled = S.Lists.Count(l => l.Enabled);
        if (snapshot.RuleCount > 0) Ok($"{snapshot.RuleCount:N0} blocking rules loaded from {enabled} lists"); else Bad("No filter rules loaded yet — open Filter Lists and update");
        foreach (var l in S.Lists.Where(l => l.Enabled && l.LastError is not null)) Bad($"List “{l.Name}”: {l.LastError} (using previous copy)");

        if (proxy is not null)
        {
            var good = await Probe("example.com");
            if (good is { } g && g.rcode == 0) Ok("Resolving normal sites works (example.com)");
            else Bad("Could not resolve example.com through the proxy — check upstream DNS");
            string? sample = new[] { "doubleclick.net", "googleadservices.com", "ads.google.com", "adservice.google.com" }.FirstOrDefault(x => snapshot.Decide(x).Blocked);
            if (sample is not null)
            {
                var bl = await Probe(sample);
                if (bl is { } b && b.nullIp) Ok($"Blocking works ({sample} → 0.0.0.0)"); else Bad($"{sample} should be blocked but was not");
            }
        }
        if (!Dev && S.PauseHotkey) { if (App.HotkeyOk) Ok("Hotkey Ctrl+Alt+H pauses or resumes protection"); else Info("Hotkey Ctrl+Alt+H could not be registered (another program may be using it)"); }
        Info(S.BlockSecureDns ? "Secure-DNS bypass protection: on (browsers are told to keep using Hushwire)" : "Secure-DNS bypass protection: off — browsers with their own secure DNS can bypass filtering (Settings → Secure DNS)");
        if (proxy?.Doh is { } doh)
        {
            if (doh.Failed > 0 && doh.Succeeded == 0) Bad($"Encrypted upstream ({doh.Provider.Name}) is not answering: {doh.LastError}. Falling back to ordinary DNS ({proxy.DohFallbacks:N0} lookups so far).");
            else Ok($"Encrypted upstream: {doh.Provider.Name} ({doh.Succeeded:N0} answered, {proxy.DohFallbacks:N0} fell back to ordinary DNS)");
        }
        else Info("Encrypted upstream: off — lookups to your upstream DNS are not encrypted (Settings → Secure DNS)");
        if (!Dev && S.AdFilter)
        {
            if (adFilter.Running) Ok($"YouTube/Twitch ad filter is running (certificate {(AdFilterCertificateInstalled ? "installed" : "MISSING from the trust store")})");
            else Bad("YouTube/Twitch ad filter is on in settings but not running" + (AdFilterError.Length > 0 ? ": " + AdFilterError : ""));
        }
        if (!Dev) Info($"DNS settings were last changed via: {Net.LastMethod}");
        var gc = GC.GetGCMemoryInfo();
        using (var self = Process.GetCurrentProcess())
            Info($"Memory: {self.WorkingSet64 / 1048576} MB in use by Hushwire ({self.PrivateMemorySize64 / 1048576} MB private); .NET heap {GC.GetTotalMemory(false) / 1048576} MB live, {gc.HeapSizeBytes / 1048576} MB reserved, {gc.FragmentedBytes / 1048576} MB fragmented; {snapshot.RuleCount:N0} rules");
        Info("Browsers with their own “Secure DNS / DNS over HTTPS” setting bypass system DNS and cannot be filtered by Hushwire.");
        return r;
    }

    /// <summary>Everything useful for a bug report, with addresses and the Windows user name masked. Contains no browsing data and none of your rules.</summary>
    public async Task<string> BuildReportAsync(RotatingLog log)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Hushwire {typeof(Protection).Assembly.GetName().Version?.ToString(3)}  ·  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}  ·  {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"State: {State} {Detail}  ·  rules: {snapshot.RuleCount:N0}  ·  your rules: {snapshot.UserRuleCount}  ·  block mode: {S.BlockMode}  ·  CNAME check: {S.CnameCheck}");
        sb.AppendLine($"YouTube/Twitch filter: {AdFilterStatus}{(AdFilterError.Length > 0 ? " (" + AdFilterError + ")" : "")}  ·  DNS last changed via: {Net.LastMethod}");
        foreach (var l in S.Lists.Where(l => l.Enabled))
            sb.AppendLine($"  list {l.Name}: {l.RuleCount:N0} rules, last success {(l.LastSuccessUtc?.ToString("u") ?? "never")}{(l.LastError is null ? "" : ", error: " + l.LastError)}");
        sb.AppendLine();
        foreach (var line in await DiagnoseAsync()) sb.AppendLine(line);
        sb.AppendLine().AppendLine("--- recent errors (newest last) ---");
        var tail = log.Tail();
        sb.AppendLine(tail.Length > 0 ? tail : "(none)");
        return Redactor.Redact(sb.ToString());
    }

    async Task<(int rcode, bool nullIp)?> Probe(string name)
    {
        try
        {
            var m = new List<byte> { 0x53, 0x56, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var l in name.Split('.')) { m.Add((byte)l.Length); m.AddRange(System.Text.Encoding.ASCII.GetBytes(l)); }
            m.AddRange([0, 0, 1, 0, 1]);
            using var c = new CancellationTokenSource(6000);
            var r = await proxy!.Resolve(m.ToArray(), new IPEndPoint(IPAddress.Loopback, 1), false, c.Token);
            if (r is null) return null;
            return (r[3] & 0x0F, r.Length >= 4 && r[^4..].All(b => b == 0) && r[7] > 0);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        cts.Cancel(); pauseTimer?.Dispose(); updateTimer?.Dispose(); saveTimer?.Dispose(); houseTimer?.Dispose();
        proxy?.Dispose();
    }
}
