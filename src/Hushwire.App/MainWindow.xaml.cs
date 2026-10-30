using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hushwire.Core;
using Microsoft.Win32;

namespace Hushwire.App;

public sealed class ListRow
{
    public ListSub Sub { get; }
    public ListRow(ListSub s) => Sub = s;
    public bool Enabled { get => Sub.Enabled; set => Sub.Enabled = value; }
    public string Name => Sub.Name;
    public string Category => Sub.Category;
    public string RulesText => Sub.RuleCount > 0 ? Sub.RuleCount.ToString("N0") : "—";
    public string LastText => Sub.LastSuccessUtc is { } t ? t.ToLocalTime().ToString("g") : "never";
    public string StatusText => Sub.LastError is { } e ? "Failed: " + e + (Sub.LastSuccessUtc is null ? "" : " — using previous copy") : Sub.LastSuccessUtc is null ? (Sub.Enabled ? "Waiting to download" : "Off") : Sub.Enabled ? "OK" : "Off";
}

public sealed class AppRow
{
    public AppStat A { get; }
    public AppRow(AppStat a, bool exempt) { A = a; Exempt = exempt; }
    public bool Exempt { get; set; }
    public bool CanExempt => A.Path.Length > 0;
    public string Name => A.Name;
    public string Path => A.Path.Length > 0 ? A.Path : "(shared Windows service — not exemptable)";
    public string QueriesText => A.Queries.ToString("N0");
    public string BlockedText => A.Blocked.ToString("N0");
}

public partial class MainWindow : Window
{
    readonly Protection prot;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int actVersion = -1;
    long dashVersion = -1; string dashStatus = "";
    readonly DispatcherTimer trim = new() { Interval = TimeSpan.FromSeconds(5) };

    void ResetCaches() { actVersion = -1; dashVersion = -1; listsSig = ""; appsSig = ""; certInstalled = null; }

    /// <summary>While the window is hidden in the tray, drop the big on-screen lists so their memory can be reclaimed.</summary>
    void ReleaseUi()
    {
        ActView.ItemsSource = null; ListsView.ItemsSource = null; AppsView.ItemsSource = null;
        AllowBox.ItemsSource = null; BlockBox.ItemsSource = null;
        CatList.ItemsSource = ListList.ItemsSource = DomList.ItemsSource = AppList.ItemsSource = null;
        ResetCaches();
    }

    public MainWindow(Protection p)
    {
        InitializeComponent();
        prot = p;
        Icon = IconFactory.Source(Color.FromRgb(0x4F, 0x8C, 0xFF));
        AboutVer.Text = $"Hushwire {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}  ·  data folder: {Paths.Dir}";
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { trim.Stop(); ResetCaches(); timer.Start(); Refresh(); } else { timer.Stop(); ReleaseUi(); trim.Start(); } };
        trim.Tick += (_, _) => { trim.Stop(); if (!IsVisible) MemoryTrim.Run(); };
        prot.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });   // nothing to draw while hidden in the tray
        LoadSettingsUi();
        SourceInitialized += (_, _) =>
        {
            int on = 1; // dark title bar
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public bool Dead { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Quitting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e) { Dead = true; base.OnClosed(e); }

    /// <summary>Developer aid: renders every page to PNGs (used to check the UI without a desktop session).</summary>
    public async System.Threading.Tasks.Task SnapshotAll(string dir)
    {
        Directory.CreateDirectory(dir);
        await prot.DiagnoseAsync();
        var navs = new (string, RadioButton)[] { ("dashboard", NavDash), ("activity", NavActivity), ("lists", NavLists), ("rules", NavRules), ("apps", NavApps), ("settings", NavSettings), ("about", NavAbout) };
        foreach (var (name, nav) in navs)
        {
            nav.IsChecked = true;
            if (name == "about") DiagList.ItemsSource = await prot.DiagnoseAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
    }

    // ------------------------------------------------------------ navigation

    FrameworkElement[] Pages => [PgDash, PgActivity, PgLists, PgRules, PgApps, PgSettings, PgAbout];

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && PgAbout is null) return;
        var map = new Dictionary<object, FrameworkElement>
        { [NavDash] = PgDash, [NavActivity] = PgActivity, [NavLists] = PgLists, [NavRules] = PgRules, [NavApps] = PgApps, [NavSettings] = PgSettings, [NavAbout] = PgAbout };
        foreach (var p in Pages) p.Visibility = Visibility.Collapsed;
        map[sender].Visibility = Visibility.Visible;
        ResetCaches();
        AllowBox.ItemsSource = null; BlockBox.ItemsSource = null; // re-read rules from settings on every visit
        Refresh();
    }

    public void Show(string page)
    {
        Show(); WindowState = WindowState.Normal; Activate();
        if (page == "lists") NavLists.IsChecked = true;
    }

    // ------------------------------------------------------------ refresh

    void Refresh()
    {
        var st = prot.State;
        var brush = (Brush)FindResource(st switch { ProtectionState.On => "Good", ProtectionState.Paused => "Warn", ProtectionState.Error => "Bad", _ => "Muted" });
        Dot.Fill = brush;
        SideNote.Text = prot.Dev ? "Developer mode — DNS settings are not changed." : "";
        var ups = prot.ResolveUpstreams();

        switch (st)
        {
            case ProtectionState.On:
                HeroTitle.Text = "You're protected"; HeroTitle.Foreground = brush;
                HeroSub.Text = $"Filtering DNS lookups from every app on this PC · {prot.Snapshot.RuleCount:N0} rules" + (prot.Detail.Length > 0 ? $"\n{prot.Detail}" : "");
                ToggleBtn.Content = "Turn off"; ToggleBtn.IsEnabled = true; PauseBtn.IsEnabled = true; break;
            case ProtectionState.Paused:
                HeroTitle.Text = $"Paused until {prot.PausedUntil:t}"; HeroTitle.Foreground = brush;
                HeroSub.Text = "Nothing is blocked while paused. Protection resumes automatically.";
                ToggleBtn.Content = "Resume"; ToggleBtn.IsEnabled = true; PauseBtn.IsEnabled = false; break;
            case ProtectionState.Starting:
                HeroTitle.Text = "Starting…"; HeroTitle.Foreground = brush; HeroSub.Text = prot.Detail;
                ToggleBtn.IsEnabled = false; PauseBtn.IsEnabled = false; break;
            case ProtectionState.Error:
                HeroTitle.Text = "Protection is off — something went wrong"; HeroTitle.Foreground = brush; HeroSub.Text = prot.Detail;
                ToggleBtn.Content = "Try again"; ToggleBtn.IsEnabled = true; PauseBtn.IsEnabled = false; break;
            default:
                HeroTitle.Text = "Protection is off"; HeroTitle.Foreground = (Brush)FindResource("Fg");
                HeroSub.Text = "DNS lookups are not being filtered.";
                ToggleBtn.Content = "Turn on"; ToggleBtn.IsEnabled = true; PauseBtn.IsEnabled = false; break;
        }

        if (PgDash.Visibility == Visibility.Visible) RefreshDash();
        else if (PgActivity.Visibility == Visibility.Visible) RefreshActivity();
        else if (PgLists.Visibility == Visibility.Visible) RefreshLists();
        else if (PgApps.Visibility == Visibility.Visible) RefreshApps();
        else if (PgRules.Visibility == Visibility.Visible && AllowBox.ItemsSource is null) RefreshRules();
        else if (PgSettings.Visibility == Visibility.Visible) { RefreshAdFilterText(); UpstreamNow.Text = ups.Count > 0 ? "Currently forwarding to: " + string.Join(", ", ups) : "No upstream detected yet — turn protection on, or enter one above."; }
    }

    void RefreshDash()
    {
        // The dashboard only changes when a counter does: skip the work (and the list rebuilds) on an idle second.
        long sv = prot.Stats.Version;
        string status = prot.ListStatus;
        if (sv == dashVersion && status == dashStatus) return;
        dashVersion = sv; dashStatus = status;
        var day = prot.Stats.Day();
        var life = prot.Stats.Lifetime;
        long allowed = day.Queries - day.Blocked;
        TQueries.Text = life.Queries.ToString("N0");
        TQueriesSub.Text = $"since {life.Since:d} · {day.Queries:N0} today";
        TBlocked.Text = life.Blocked.ToString("N0");
        TBlockedSub.Text = $"{day.Blocked:N0} today";
        TAllowed.Text = (life.Queries - life.Blocked).ToString("N0");
        TAllowedSub.Text = $"{allowed:N0} today · {life.Cached:N0} cached";
        TRate.Text = life.Queries > 0 ? $"{100.0 * life.Blocked / life.Queries:0.#}%" : "—";
        TRateSub.Text = day.Queries > 0 ? $"{100.0 * day.Blocked / day.Queries:0.#}% today" : "";
        var cats = prot.Stats.ByCategory();
        CatList.ItemsSource = cats; ListList.ItemsSource = prot.Stats.ByList();
        EmptyCat.Visibility = cats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DomList.ItemsSource = prot.Stats.TopDomains();
        AppList.ItemsSource = prot.Stats.TopApps();
        Footnote.Text = $"Counts are DNS lookups for hostnames on your blocklists — not individual ads, banners or cookies. · {prot.ListStatus}" +
            (life.Cname > 0 ? $" · {life.Cname:N0} lookups blocked as CNAME-disguised trackers" : "") +
            (prot.Stats.Rewritten is var (ry, rt) && ry + rt > 0 ? $" · in-page ad filter removed {ry:N0} items on YouTube, {rt:N0} on Twitch" : "") +
            (life.Failures > 0 ? $" · {life.Failures:N0} lookups got no answer from the upstream DNS" : "");
    }

    void RefreshActivity()
    {
        var f = ActFilter.Text.Trim();
        // new rows must not yank the list from under a selection, but typing a filter always applies
        if (ActView.SelectedItem is not null && f == lastFilter) return;
        if (prot.ActivityVersion == actVersion && f == lastFilter) return;       // nothing new: don't even copy the list
        var (v, rows) = prot.ActivitySnapshot();
        actVersion = v; lastFilter = f;
        ActView.ItemsSource = f.Length == 0 ? rows : rows.Where(r => r.Domain.Contains(f, StringComparison.OrdinalIgnoreCase) || r.App.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Source.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    string lastFilter = "";

    void ListSel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ListsView.SelectedItem is not ListRow r) { ListDetail.Text = "Select a list to see what it blocks and its license."; return; }
        var s = r.Sub;
        ListDetail.Text = $"{s.Name} — {s.Description}{Environment.NewLine}License: {(s.License.Length > 0 ? s.License : "unknown")}  ·  {s.Url}" + (s.UnsupportedCount > 0 ? $"  ·  {s.UnsupportedCount:N0} rules can't be used at DNS level and are skipped" : "");
    }

    string listsSig = "";

    /// <summary>Only rebuilds the grid when a list actually changed, and keeps the selected row — this runs every second.</summary>
    void RefreshLists()
    {
        ListsStatus.Text = prot.ListStatus;
        var sig = string.Join(";", prot.S.Lists.Select(l => $"{l.Id}|{l.Enabled}|{l.RuleCount}|{l.LastSuccessUtc:O}|{l.LastError}"));
        if (sig == listsSig) return;
        listsSig = sig;
        var selId = (ListsView.SelectedItem as ListRow)?.Sub.Id;
        var rows = prot.S.Lists.Select(l => new ListRow(l)).ToList();
        ListsView.ItemsSource = rows;
        if (selId is not null) ListsView.SelectedItem = rows.FirstOrDefault(r => r.Sub.Id == selId);
    }

    void RefreshRules()
    {
        AllowBox.ItemsSource = prot.S.UserAllow.OrderBy(x => x).ToList();
        BlockBox.ItemsSource = prot.S.UserBlock.OrderBy(x => x).ToList();
    }

    string appsSig = "";

    void RefreshApps()
    {
        var seen = prot.Stats.Apps();
        var sig = string.Join(";", seen.Select(a => $"{a.Path}|{a.Name}|{a.Queries}|{a.Blocked}"));
        if (sig == appsSig) return;
        appsSig = sig;
        AppsView.ItemsSource = seen.Select(a => new AppRow(a, a.Path.Length > 0 && prot.S.ExemptApps.Contains(a.Path, StringComparer.OrdinalIgnoreCase))).ToList();
    }

    // ------------------------------------------------------------ dashboard actions

    async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        switch (prot.State)
        {
            case ProtectionState.Paused: prot.Resume(); break;
            case ProtectionState.On: await prot.DisableAsync(); break;
            default: await prot.EnableAsync(); break;
        }
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseBtn.ContextMenu!.PlacementTarget = PauseBtn;
        PauseBtn.ContextMenu.IsOpen = true;
    }

    void PauseItem_Click(object sender, RoutedEventArgs e)
    {
        int m = int.Parse((string)((MenuItem)sender).Tag);
        if (m == 0) prot.Resume(); else prot.Pause(TimeSpan.FromMinutes(m));
    }

    // ------------------------------------------------------------ activity

    void ActFilter_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) { actVersion = -1; RefreshActivity(); } }
    void ClearActivity_Click(object sender, RoutedEventArgs e) { prot.ClearActivity(); ActView.ItemsSource = null; actVersion = -1; }
    void LogAllowed_Click(object sender, RoutedEventArgs e) { prot.S.LogAllowed = LogAllowedChk.IsChecked == true; prot.Save(); }

    async void ActAllow_Click(object sender, RoutedEventArgs e) => await ActRule(true);
    async void ActBlock_Click(object sender, RoutedEventArgs e) => await ActRule(false);

    async System.Threading.Tasks.Task ActRule(bool allow)
    {
        if (ActView.SelectedItem is not ActivityRow r) { MessageBox.Show(this, "Select a row first.", "Hushwire"); return; }
        var d = Domain.Normalize(r.Domain);
        if (d is null) return;
        var target = allow ? prot.S.UserAllow : prot.S.UserBlock;
        var other = allow ? prot.S.UserBlock : prot.S.UserAllow;
        other.Remove(d);
        if (!target.Contains(d)) target.Add(d);
        await prot.ApplyRulesAsync();
        ActView.SelectedItem = null;
        AllowBox.ItemsSource = null; BlockBox.ItemsSource = null;
    }

    // ------------------------------------------------------------ lists

    async void UpdateAll_Click(object sender, RoutedEventArgs e) { await prot.UpdateListsAsync(true); RefreshLists(); }

    async void UpdateSel_Click(object sender, RoutedEventArgs e)
    {
        if (ListsView.SelectedItem is ListRow r) { await prot.UpdateListsAsync(true, r.Sub.Id); RefreshLists(); }
    }

    async void ListToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ListRow r) return;
        prot.Save();
        if (r.Sub.Enabled && r.Sub.LastSuccessUtc is null) await prot.UpdateListsAsync(false, r.Sub.Id);
        else await prot.RebuildAsync();
        RefreshLists();
    }

    async void RemoveList_Click(object sender, RoutedEventArgs e)
    {
        if (ListsView.SelectedItem is not ListRow r) return;
        if (r.Sub.Builtin) { MessageBox.Show(this, "Built-in lists can be switched off but not removed.", "Hushwire"); return; }
        prot.S.Lists.Remove(r.Sub);
        foreach (var ext in new[] { ".txt", ".prev" }) try { File.Delete(Path.ChangeExtension(prot.Lists.CurrentPath(r.Sub.Id), ext)); } catch { }
        prot.Save(); await prot.RebuildAsync(); RefreshLists();
    }

    async void AddList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window { Title = "Add filter list", Width = 520, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("Bg"), ResizeMode = ResizeMode.NoResize };
        var name = new TextBox(); var url = new TextBox();
        var cat = new[] { "Ads", "Trackers", "Malware", "Custom" }.Select((c, i) => new RadioButton { Content = c, GroupName = "c", IsChecked = i == 0, Margin = new Thickness(0, 0, 14, 0) }).ToList();
        var ok = new Button { Content = "Add", Style = (Style)FindResource("Primary"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = "Name" }); sp.Children.Add(name);
        sp.Children.Add(new TextBlock { Text = "URL (https only; hosts, domain list or Adblock format)", Margin = new Thickness(0, 10, 0, 0) }); sp.Children.Add(url);
        var cp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; foreach (var r in cat) cp.Children.Add(r);
        sp.Children.Add(cp); sp.Children.Add(ok);
        dlg.Content = sp;
        ok.Click += (_, _) => dlg.DialogResult = true;
        if (dlg.ShowDialog() != true) return;

        var u = url.Text.Trim();
        if (!ListManager.IsHttps(u)) { MessageBox.Show(this, "Only https:// URLs are accepted.", "Hushwire"); return; }
        if (prot.S.Lists.Any(l => l.Url.Equals(u, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "That list is already added.", "Hushwire"); return; }
        var id = "custom-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u)))[..10].ToLowerInvariant();
        var n = name.Text.Trim(); if (n.Length == 0) n = new Uri(u).Host;
        prot.S.Lists.Add(new ListSub { Id = id, Name = n.Length > 80 ? n[..80] : n, Url = u, Category = cat.First(r => r.IsChecked == true).Content.ToString()!, Enabled = true });
        prot.Save();
        await prot.UpdateListsAsync(true, id);
        RefreshLists();
    }

    // ------------------------------------------------------------ rules

    async void AddAllow_Click(object sender, RoutedEventArgs e) => await AddRule(AllowIn, prot.S.UserAllow, prot.S.UserBlock);
    async void AddBlock_Click(object sender, RoutedEventArgs e) => await AddRule(BlockIn, prot.S.UserBlock, prot.S.UserAllow);
    void AllowIn_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) AddAllow_Click(s, e); }
    void BlockIn_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) AddBlock_Click(s, e); }
    void ExplainIn_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) Explain_Click(s, e); }

    async System.Threading.Tasks.Task AddRule(TextBox box, List<string> target, List<string> other)
    {
        var d = Domain.Normalize(box.Text);
        if (d is null) { MessageBox.Show(this, "That isn't a valid domain name (example: ads.example.com).", "Hushwire"); return; }
        other.Remove(d);
        if (!target.Contains(d)) target.Add(d);
        box.Clear();
        await prot.ApplyRulesAsync(); RefreshRules();
    }

    async void RemAllow_Click(object sender, RoutedEventArgs e) { if (AllowBox.SelectedItem is string s) { prot.S.UserAllow.Remove(s); await prot.ApplyRulesAsync(); RefreshRules(); } }
    async void RemBlock_Click(object sender, RoutedEventArgs e) { if (BlockBox.SelectedItem is string s) { prot.S.UserBlock.Remove(s); await prot.ApplyRulesAsync(); RefreshRules(); } }
    void Explain_Click(object sender, RoutedEventArgs e) => ExplainOut.Text = prot.Explain(ExplainIn.Text);

    async void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Text files|*.txt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (new FileInfo(dlg.FileName).Length > 1_000_000) { MessageBox.Show(this, "File too large (limit 1 MB).", "Hushwire"); return; }
        int added = 0, skipped = 0;
        foreach (var line in File.ReadLines(dlg.FileName).Take(20000))
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            var parts = t.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] is not ("allow" or "block")) { skipped++; continue; }
            var d = Domain.Normalize(parts[1]);
            if (d is null) { skipped++; continue; }
            var (tg, ot) = parts[0] == "allow" ? (prot.S.UserAllow, prot.S.UserBlock) : (prot.S.UserBlock, prot.S.UserAllow);
            ot.Remove(d); if (!tg.Contains(d)) { tg.Add(d); added++; }
        }
        await prot.ApplyRulesAsync(); RefreshRules();
        MessageBox.Show(this, $"Imported {added} rules, skipped {skipped} invalid lines.\nFormat: one rule per line — “allow example.com” or “block example.com”.", "Hushwire");
    }

    void ExportRules_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Text files|*.txt", FileName = "hushwire-rules.txt" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllLines(dlg.FileName, prot.S.UserAllow.Select(d => "allow " + d).Concat(prot.S.UserBlock.Select(d => "block " + d)));
    }

    // ------------------------------------------------------------ apps

    void AppExempt_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRow r || r.A.Path.Length == 0) return;
        if (r.Exempt) { if (!prot.S.ExemptApps.Contains(r.A.Path, StringComparer.OrdinalIgnoreCase)) prot.S.ExemptApps.Add(r.A.Path); }
        else prot.S.ExemptApps.RemoveAll(p => p.Equals(r.A.Path, StringComparison.OrdinalIgnoreCase));
        prot.RefreshExempt(); prot.Save();
    }

    // ------------------------------------------------------------ settings

    void LoadSettingsUi()
    {
        var s = prot.S;
        AutoStartChk.IsChecked = s.StartWithWindows; CnameChk.IsChecked = s.CnameCheck; LogAllowedChk.IsChecked = s.LogAllowed;
        (s.BlockMode == "Nxdomain" ? ModeNx : ModeNull).IsChecked = true;
        SecureDnsChk.IsChecked = s.BlockSecureDns;
        if (EncDnsBox.ItemsSource is null)
            EncDnsBox.ItemsSource = new[] { new KeyValuePair<string, string>("Off", "Off (use my normal DNS servers)") }
                .Concat(DohUpstream.Providers.Select(p => new KeyValuePair<string, string>(p.Id, p.Name))).ToList();
        loadingSettings = true; EncDnsBox.SelectedValue = s.EncryptedDns; loadingSettings = false;
        EncDnsNow.Text = s.EncryptedDns == "Off" ? "" : $"Using {DohUpstream.Find(s.EncryptedDns)?.Name}. Takes effect immediately while protection is on.";
        UpstreamIn.Text = s.UpstreamOverride;
        AdFilterChk.IsChecked = s.AdFilter;
        if (prot.Dev) { AutoStartChk.IsEnabled = false; AdFilterChk.IsEnabled = false; }
    }

    async void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        prot.S.StartWithWindows = AutoStartChk.IsChecked == true; prot.Save();
        var (code, output) = await Ps.SetTask("HushwireApp", prot.S.StartWithWindows, "logon");
        if (code != 0) { MessageBox.Show(this, "Could not change the startup task:\n" + output, "Hushwire"); prot.S.StartWithWindows = false; AutoStartChk.IsChecked = false; prot.Save(); }
    }

    bool? certInstalled;   // reading the Windows certificate store is slow-ish; refreshed on page visit and after changes

    void RefreshAdFilterText()
    {
        bool installed = certInstalled ??= prot.AdFilterCertificateInstalled;
        string status = prot.AdFilterStatus;
        if (prot.AdFilterError.Length > 0) status = "Could not start: " + prot.AdFilterError;
        else if (prot.S.AdFilter && prot.State is not (ProtectionState.On or ProtectionState.Paused)) status = "Will start when protection is on";
        var (yt, tw) = prot.Stats.Rewritten;
        AdFilterNow.Text = $"Status: {status}  ·  certificate {(installed ? "installed" : "not installed")}  ·  removed so far: {yt:N0} on YouTube, {tw:N0} on Twitch";
        AdFilterRemoveBtn.IsEnabled = installed || prot.S.AdFilter;
        if (!prot.Dev) AdFilterChk.IsChecked = prot.S.AdFilter;
    }

    async void AdFilter_Click(object sender, RoutedEventArgs e)
    {
        if (AdFilterChk.IsChecked == true)
        {
            var ok = MessageBox.Show(this,
                """
                To remove ads inside YouTube and Twitch, Hushwire will:

                  • create a private certificate on this PC and add it to Windows' trusted certificates,
                  • then read and edit your traffic to youtube.com and Twitch's video servers (only those), locally, on this PC.

                The certificate is restricted so it cannot vouch for any other website, and its key is stored where only administrators can read it. You can remove it any time from this page.

                Turn it on?
                """,
                "Filter YouTube and Twitch ads", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) { AdFilterChk.IsChecked = false; return; }
            await prot.EnableAdFilterAsync();
            if (prot.State is not (ProtectionState.On or ProtectionState.Paused))
                MessageBox.Show(this, "The filter is set up and will start the next time protection is on.", "Hushwire");
        }
        else prot.DisableAdFilter();
        certInstalled = null;
        RefreshAdFilterText();
    }

    void AdFilterRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Turn the filter off and delete its certificate and keys from this PC?", "Hushwire", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        prot.RemoveAdFilterCertificate();
        AdFilterChk.IsChecked = false;
        certInstalled = null;
        RefreshAdFilterText();
    }

    bool loadingSettings;
    void SecureDns_Click(object sender, RoutedEventArgs e) { prot.S.BlockSecureDns = SecureDnsChk.IsChecked == true; prot.Save(); prot.Proxy?.Cache.Clear(); if (!prot.Dev) DnsApi.Flush(); }
    void EncDns_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || EncDnsBox.SelectedValue is not string id) return;
        prot.S.EncryptedDns = id; prot.Save(); prot.ApplyEncryptedUpstream();
        EncDnsNow.Text = id == "Off" ? "" : $"Using {DohUpstream.Find(id)?.Name}. Takes effect immediately while protection is on.";
    }
    void Cname_Click(object sender, RoutedEventArgs e) { prot.S.CnameCheck = CnameChk.IsChecked == true; prot.Save(); }
    void Mode_Click(object sender, RoutedEventArgs e) { prot.S.BlockMode = ModeNx.IsChecked == true ? "Nxdomain" : "NullIp"; prot.Save(); }

    void SaveUpstream_Click(object sender, RoutedEventArgs e)
    {
        var parts = UpstreamIn.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => !System.Net.IPAddress.TryParse(p, out var ip) || System.Net.IPAddress.IsLoopback(ip)))
        { MessageBox.Show(this, "Enter valid, non-loopback IP addresses (for example 192.168.1.1).", "Hushwire"); return; }
        prot.S.UpstreamOverride = string.Join(" ", parts); prot.Save();
        prot.Proxy?.Cache.Clear();
        Refresh();
    }

    async void Repair_Click(object sender, RoutedEventArgs e)
    {
        await prot.DisableAsync();
        MessageBox.Show(this, prot.State == ProtectionState.Error ? "Repair failed:\n" + prot.Detail : "Network settings are restored to what they were before Hushwire. Protection is off.", "Hushwire");
    }

    void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset all statistics?", "Hushwire", MessageBoxButton.OKCancel) == MessageBoxResult.OK) prot.Stats.Reset();
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Paths.Ensure();
        Process.Start(new ProcessStartInfo("explorer.exe", Paths.Dir) { UseShellExecute = true });
    }

    // The license and notices are installed next to the exe; if they're missing (portable exe), open the published copies instead.
    static void OpenBundled(string file, string fallbackUrl)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);
        Process.Start(new ProcessStartInfo(File.Exists(path) ? path : fallbackUrl) { UseShellExecute = true });
    }

    void License_Click(object sender, RoutedEventArgs e) => OpenBundled("LICENSE.txt", "https://www.gnu.org/licenses/gpl-3.0.html");
    void Notices_Click(object sender, RoutedEventArgs e) => OpenBundled("THIRD_PARTY_NOTICES.md", "https://github.com/gbogdev/Hushwire/blob/main/THIRD_PARTY_NOTICES.md");

    async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Text files|*.txt", FileName = $"hushwire-report-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dlg.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dlg.FileName, await prot.BuildReportAsync(App.ErrorLog)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Could not save the report: " + ex.Message, "Hushwire"); }
    }

    async void Diag_Click(object sender, RoutedEventArgs e)
    {
        DiagList.ItemsSource = new[] { "Running checks…" };
        DiagList.ItemsSource = await prot.DiagnoseAsync();
    }
}
