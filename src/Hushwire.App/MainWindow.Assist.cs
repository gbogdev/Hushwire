using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hushwire.Core;

namespace Hushwire.App;

/// <summary>The "something broke" fixer, the self-check line, schedules, protection level, notifications and keyboard shortcuts.</summary>
public partial class MainWindow
{
    Window MakeDialog(string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = (Brush)FindResource("Bg"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
    };

    TextBlock Muted(string text, double top = 0) => new() { Text = text, Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };

    // ------------------------------------------------------------ something broke

    void Fix_Click(object sender, RoutedEventArgs e) => OpenFixer();

    public void OpenFixer()
    {
        var suspects = prot.FindSuspects();
        var dlg = MakeDialog("Something broke?", 640);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Something broke?", Style = (Style)FindResource("H1") });
        var close = new Button { Content = "Close", Style = (Style)FindResource("Btn"), IsCancel = true };

        if (suspects.Count == 0)
        {
            root.Children.Add(Muted("Nothing was blocked in the last 10 minutes, so Hushwire is probably not the cause. To be sure, pause protection for 5 minutes and try again; if it still fails, the problem is elsewhere.", 8));
            var test = new Button { Content = "Pause for 5 minutes", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 14, 8, 0) };
            test.Click += (_, _) => { prot.Pause(TimeSpan.FromMinutes(5)); dlg.Close(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            close.Margin = new Thickness(0, 14, 0, 0);
            row.Children.Add(test); row.Children.Add(close); root.Children.Add(row);
        }
        else
        {
            root.Children.Add(Muted("These lookups were blocked in the last 10 minutes, most likely culprit first. Pick one and allow it for an hour: it switches itself off again, so nothing is left open by accident.", 6));
            var list = new ListView { Margin = new Thickness(0, 12, 0, 0), MaxHeight = 300, SelectionMode = SelectionMode.Single };
            var grid = new GridView();
            grid.Columns.Add(new GridViewColumn { Header = "Domain", Width = 230, DisplayMemberBinding = new System.Windows.Data.Binding("Domain") });
            grid.Columns.Add(new GridViewColumn { Header = "Why it ranks here", Width = 340, DisplayMemberBinding = new System.Windows.Data.Binding("Reason") });
            list.View = grid; list.ItemsSource = suspects; list.SelectedIndex = 0;
            root.Children.Add(list);

            var hour = new Button { Content = "Allow for 1 hour", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 8, 0) };
            var always = new Button { Content = "Allow always", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            var pause = new Button { Content = "Pause 5 min to test", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            hour.Click += async (_, _) => { if (list.SelectedItem is Suspect s) { await prot.AllowTemporarilyAsync(s.Domain, TimeSpan.FromHours(1)); dlg.Close(); MessageBox.Show(this, $"{s.Domain} is allowed for the next hour. Try again now.", "Hushwire"); } };
            always.Click += async (_, _) => { if (list.SelectedItem is Suspect s) { await prot.AllowPermanentlyAsync(s.Domain); dlg.Close(); } };
            pause.Click += (_, _) => { prot.Pause(TimeSpan.FromMinutes(5)); dlg.Close(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(hour); row.Children.Add(always); row.Children.Add(pause); row.Children.Add(close);
            root.Children.Add(row);
        }
        dlg.Content = root;
        dlg.ShowDialog();
    }

    // ------------------------------------------------------------ self-check line

    void RefreshHealthLine()
    {
        var h = prot.LastHealth;
        if (h is null || prot.State is ProtectionState.Off or ProtectionState.Error)
        {
            HealthLine.Text = prot.State is ProtectionState.On or ProtectionState.Paused ? "Self-check: running…" : "";
            HealthLine.Foreground = (Brush)FindResource("Muted");
            return;
        }
        HealthLine.Text = $"Self-check: {h.Word}. {h.Summary}";
        HealthLine.Foreground = (Brush)FindResource(h.Level switch { HealthLevel.Protected => "Good", HealthLevel.Partial => "Warn", HealthLevel.NotProtected => "Bad", _ => "Muted" });
    }

    async void Health_Click(object sender, RoutedEventArgs e)
    {
        HealthLine.Text = "Self-check: running…";
        await prot.SelfCheckAsync();
        RefreshHealthLine();
    }

    // ------------------------------------------------------------ settings: level, schedules, notifications

    void LoadAssistSettingsUi()
    {
        var s = prot.S;
        if (ProfilePanel.Children.Count == 0)
            foreach (var p in ListProfiles.All)
            {
                var rb = new RadioButton { Content = $"{p.Name}: {p.Description}", Tag = p.Id, GroupName = "profile" };
                rb.Click += Profile_Click;
                ProfilePanel.Children.Add(rb);
            }
        foreach (RadioButton rb in ProfilePanel.Children) rb.IsChecked = (string)rb.Tag == s.Profile;
        RefreshSchedules();
        NotifyChk.IsChecked = s.Notifications; HotkeyChk.IsChecked = s.PauseHotkey;
        if (prot.Dev) HotkeyChk.IsEnabled = false;
    }

    async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string id }) return;
        await prot.SetProfileAsync(id);
    }

    void RefreshSchedules() => SchedBox.ItemsSource = prot.S.Schedules.Select(Schedules.Describe).ToList();

    void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        int i = SchedBox.SelectedIndex;
        if (i < 0 || i >= prot.S.Schedules.Count) return;
        prot.S.Schedules.RemoveAt(i); prot.Save(); RefreshSchedules();
    }

    void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (prot.S.Schedules.Count >= 20) { MessageBox.Show(this, "That is the most schedules Hushwire keeps (20).", "Hushwire"); return; }
        var dlg = MakeDialog("Add schedule", 480);
        var name = new TextBox { MaxLength = 40 };
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }.Select((d, i) => new CheckBox { Content = d, IsChecked = true, Margin = new Thickness(0, 0, 10, 0), Tag = i }).ToList();
        var from = new TextBox { Text = "22:00", Width = 70 }; var to = new TextBox { Text = "07:00", Width = 70 };
        var actions = new List<(RadioButton rb, string action)>
        {
            (new RadioButton { Content = "Pause protection", IsChecked = true, GroupName = "a" }, "Pause"),
            (new RadioButton { Content = "Use the Light level", GroupName = "a" }, "Profile:light"),
            (new RadioButton { Content = "Use the Strict level", GroupName = "a" }, "Profile:strict"),
        };
        var err = new TextBlock { Foreground = (Brush)FindResource("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Add", Style = (Style)FindResource("Primary"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };

        var sp = new StackPanel { Margin = new Thickness(22) };
        sp.Children.Add(new TextBlock { Text = "Add schedule", Style = (Style)FindResource("H1") });
        sp.Children.Add(Muted("Name (optional)", 12)); sp.Children.Add(name);
        sp.Children.Add(Muted("Days", 10));
        var dp = new WrapPanel(); foreach (var d in days) dp.Children.Add(d); sp.Children.Add(dp);
        sp.Children.Add(Muted("From and to (24-hour; if \"to\" is earlier it runs past midnight)", 10));
        var tp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        tp.Children.Add(from); tp.Children.Add(new TextBlock { Text = "  to  ", VerticalAlignment = VerticalAlignment.Center }); tp.Children.Add(to);
        sp.Children.Add(tp);
        sp.Children.Add(Muted("What happens", 10));
        foreach (var (rb, _) in actions) sp.Children.Add(rb);
        sp.Children.Add(err); sp.Children.Add(ok);
        dlg.Content = sp;

        ok.Click += (_, _) =>
        {
            if (!TryTime(from.Text, out int a) || !TryTime(to.Text, out int b)) { err.Text = "Enter times like 22:00 or 7:30."; return; }
            int mask = days.Where(d => d.IsChecked == true).Sum(d => 1 << (int)d.Tag);
            if (mask == 0) { err.Text = "Pick at least one day."; return; }
            prot.S.Schedules.Add(new ScheduleRule { Name = name.Text.Trim(), Days = mask, StartMinute = a, EndMinute = b, Action = actions.First(x => x.rb.IsChecked == true).action });
            prot.Save(); RefreshSchedules(); dlg.DialogResult = true;
        };
        dlg.ShowDialog();
    }

    static bool TryTime(string text, out int minute)
    {
        minute = 0;
        var p = text.Trim().Split(':');
        if (p.Length != 2 || !int.TryParse(p[0], out int h) || !int.TryParse(p[1], out int m) || h is < 0 or > 23 || m is < 0 or > 59) return false;
        minute = h * 60 + m; return true;
    }

    void Notify_Click(object sender, RoutedEventArgs e) { prot.S.Notifications = NotifyChk.IsChecked == true; prot.Save(); }

    void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        prot.S.PauseHotkey = HotkeyChk.IsChecked == true; prot.Save();
        (Application.Current as App)?.ApplyHotkey();
    }

    // ------------------------------------------------------------ keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var navs = new[] { NavDash, NavActivity, NavLists, NavRules, NavApps, NavSettings, NavAbout };
        int i = e.Key - Key.D1;
        if (i >= 0 && i < navs.Length) { navs[i].IsChecked = true; e.Handled = true; }
    }

    // ------------------------------------------------------------ rules page note

    void RefreshTimedNote()
    {
        var active = prot.S.TimedAllows.Where(a => a.ExpiresUtc > DateTime.UtcNow).OrderBy(a => a.ExpiresUtc).ToList();
        TimedNote.Text = active.Count == 0 ? "" : "Allowed for a limited time: " + string.Join(", ", active.Select(a => $"{a.Domain} (until {a.ExpiresUtc.ToLocalTime():t})"));
        TimedNote.Visibility = active.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
