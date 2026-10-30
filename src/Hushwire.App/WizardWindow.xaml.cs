using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hushwire.Core;

namespace Hushwire.App;

/// <summary>Three short steps on the very first start: what it does, how strict, then turn it on and check it works.</summary>
public partial class WizardWindow : Window
{
    readonly Protection prot;
    int step;
    bool turnedOn, busy;

    public WizardWindow(Protection p)
    {
        InitializeComponent();
        prot = p;
        foreach (var l in ListProfiles.All)
            LevelPanel.Children.Add(new RadioButton { Content = $"{l.Name}: {l.Description}", Tag = l.Id, GroupName = "lvl", IsChecked = l.Id == p.S.Profile });
        Show(0);
    }

    void Show(int s)
    {
        step = s;
        Step1.Visibility = s == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = s == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = s == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepTitle.Text = s switch { 0 => "Welcome to Hushwire", 1 => "How strict?", _ => "Turn it on" };
        StepCount.Text = $"Step {s + 1} of 3";
        BackBtn.Visibility = s == 1 ? Visibility.Visible : Visibility.Collapsed;
        SkipBtn.Visibility = turnedOn ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Content = s switch { 0 => "Next", 1 => "Next", _ => turnedOn ? "Finish" : "Turn on and check" };
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (!busy && step > 0) Show(step - 1); }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (step < 2)
        {
            if (step == 1) await ApplyChoicesAsync();
            Show(step + 1); return;
        }
        if (turnedOn) { Finish(); return; }

        busy = true; NextBtn.IsEnabled = false; BackBtn.IsEnabled = false;
        NextBtn.Content = "Working…";
        try
        {
            await prot.EnableAsync();
            var h = await prot.SelfCheckAsync();
            ResultCard.Visibility = Visibility.Visible;
            ResultTitle.Text = prot.State == ProtectionState.On ? h.Word : "Could not turn on";
            ResultText.Text = prot.State == ProtectionState.On ? (h.Level == HealthLevel.Protected ? "Everything checked is working. You can close this and carry on." : string.Join("\n", h.Problems)) : prot.Detail;
            ResultTitle.Foreground = (Brush)FindResource(prot.State != ProtectionState.On ? "Bad" : h.Level switch { HealthLevel.Protected => "Good", HealthLevel.Partial => "Warn", _ => "Bad" });
            turnedOn = prot.State == ProtectionState.On;
        }
        finally { busy = false; NextBtn.IsEnabled = true; BackBtn.IsEnabled = true; }
        Show(2);
        if (!turnedOn) NextBtn.Content = "Try again";
    }

    async System.Threading.Tasks.Task ApplyChoicesAsync()
    {
        prot.S.Notifications = NotifyBox.IsChecked == true;
        prot.S.PauseHotkey = HotkeyBox.IsChecked == true;
        prot.Save();
        (Application.Current as App)?.ApplyHotkey();
        var pick = LevelPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        if (pick is not null && pick != prot.S.Profile) await prot.SetProfileAsync(pick);
    }

    void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    void Finish() { prot.S.FirstRunDone = true; prot.Save(); Close(); }

    protected override void OnClosed(EventArgs e)
    {
        if (!prot.S.FirstRunDone) { prot.S.FirstRunDone = true; prot.Save(); }      // closing the window counts as skipping: it only ever shows once
        base.OnClosed(e);
    }
}
