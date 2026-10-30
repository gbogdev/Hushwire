using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Hushwire.Core;
using Forms = System.Windows.Forms;

namespace Hushwire.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless modes run before any WPF is created so they stay tiny.
        if (args.Contains("--restore"))
            try { new NetConfig().RestoreAsync().GetAwaiter().GetResult(); return 0; } catch { return 1; }
        bool devMode = args.Contains("--dev");
        if (args.Contains("--stop")) return Lifecycle.Stop(devMode, TimeSpan.FromSeconds(25));      // used by the installer before upgrade/uninstall
        if (args.Contains("--uninstall")) return Lifecycle.Uninstall(devMode);                             // used by the installer on uninstall
        int w = Array.IndexOf(args, "--watchdog");
        if (w >= 0 && w + 1 < args.Length && int.TryParse(args[w + 1], out var pid))
        {
            // If the main process dies without restoring DNS (crash, kill), put the network back.
            try { Process.GetProcessById(pid).WaitForExit(); } catch (ArgumentException) { }
            var net = new NetConfig();
            if (net.HasJournal) try { net.RestoreAsync().GetAwaiter().GetResult(); } catch { }
            return 0;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

public static class IconFactory
{
    public static Icon Make(System.Windows.Media.Color c, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            // the Hushwire shield and arrow, filled with the status colour (green on, amber paused, red problem, grey off)
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float S(float v) => v * size;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddLine(S(.5f), S(.04f), S(.92f), S(.19f));
            path.AddLine(S(.92f), S(.19f), S(.92f), S(.50f));
            path.AddBezier(S(.92f), S(.50f), S(.92f), S(.76f), S(.74f), S(.90f), S(.5f), S(.97f));
            path.AddBezier(S(.5f), S(.97f), S(.26f), S(.90f), S(.08f), S(.76f), S(.08f), S(.50f));
            path.AddLine(S(.08f), S(.50f), S(.08f), S(.19f));
            path.CloseFigure();
            using var br = new SolidBrush(Color.FromArgb(c.R, c.G, c.B));
            g.FillPath(br, path);
            using var pen = new Pen(Color.FromArgb(0x0F, 0x1A, 0x2B), Math.Max(1.6f, size * 0.11f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLine(pen, S(.5f), S(.74f), S(.5f), S(.34f));
            g.DrawLines(pen, new[] { new PointF(S(.33f), S(.51f)), new PointF(S(.5f), S(.34f)), new PointF(S(.67f), S(.51f)) });
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public static BitmapSource Source(System.Windows.Media.Color c)
    {
        using var ico = Make(c, 64);
        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

public partial class App : Application
{
    public static bool Quitting;
    Mutex? mutex;
    EventWaitHandle? showEvent;
    Protection? prot;
    MainWindow? win;
    Forms.NotifyIcon? tray;
    Forms.ToolStripMenuItem? statusItem, toggleItem;
    ProtectionState lastTrayState = (ProtectionState)(-1);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // The UI is flat text and cards. With hardware rendering WPF created ~1,000 extra event handles and ~60 MB of
        // graphics memory while the window was open; software rendering needs neither and is plenty fast for this.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        bool dev = e.Args.Contains("--dev"), startHidden = e.Args.Contains("--tray");

        mutex = new Mutex(true, Lifecycle.Name("App", dev), out bool first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Lifecycle.Name("Show", dev));
        if (!first) { showEvent.Set(); Shutdown(); return; }
        _ = Task.Run(() => { while (showEvent.WaitOne()) Dispatcher.BeginInvoke(() => ShowMain()); });
        var quitEvent = Lifecycle.CreateQuitEvent(dev);     // the installer signals this to close a running copy cleanly
        _ = Task.Run(() => { while (quitEvent.WaitOne()) Dispatcher.BeginInvoke(() => QuitNow()); });

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; Tell(a.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { Log(a.ExceptionObject as Exception); prot?.ShutdownRestore(); };
        SessionEnding += (_, _) => { Quitting = true; prot?.ShutdownRestore(); };

        try
        {
            Paths.Ensure();
            prot = new Protection(SettingsStore.Load(), dev);
            win = new MainWindow(prot);
            BuildTray();
            prot.Changed += () => Dispatcher.BeginInvoke(UpdateTray);
            prot.Notice += (title, msg) => Dispatcher.BeginInvoke(() => tray?.ShowBalloonTip(6000, title, msg, Forms.ToolTipIcon.Info));
            if (!startHidden) win.Show();
            UpdateTray();
            await prot.InitAsync();
            ApplyHotkey();
            int si = Array.IndexOf(e.Args, "--snapshot");
            if (si < 0 && !prot.S.FirstRunDone && !startHidden) new WizardWindow(prot) { Owner = win }.ShowDialog();
            if (si >= 0 && !prot.S.FirstRunDone) { prot.S.FirstRunDone = true; }
            if (si >= 0 && si + 1 < e.Args.Length)
            {
                await Task.Delay(8000);
                await win.SnapshotAll(e.Args[si + 1]);
                Quitting = true; Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show("Hushwire could not start:\n" + ex.Message, "Hushwire", MessageBoxButton.OK, MessageBoxImage.Error);
            Quitting = true; prot?.ShutdownRestore(); Shutdown();
        }
    }

    // ---- Ctrl+Alt+H: pause or resume from anywhere (a message-only window receives the hot key)
    const int WM_HOTKEY = 0x0312, HotkeyId = 1;
    HwndSource? hotkeySource;
    bool hotkeyRegistered;
    /// <summary>Whether Ctrl+Alt+H is currently registered (another program may already own it).</summary>
    public static bool HotkeyOk { get; private set; }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    /// <summary>Registers or releases the hot key to match the setting.</summary>
    public void ApplyHotkey()
    {
        try
        {
            bool want = prot is { Dev: false } && prot.S.PauseHotkey;
            if (hotkeyRegistered && !want) { UnregisterHotKey(hotkeySource!.Handle, HotkeyId); hotkeyRegistered = false; HotkeyOk = false; }
            if (!want || hotkeyRegistered) return;
            if (hotkeySource is null)
            {
                hotkeySource = new HwndSource(new HwndSourceParameters("HushwireHotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });   // HWND_MESSAGE
                hotkeySource.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == WM_HOTKEY && w.ToInt32() == HotkeyId) { prot?.TogglePauseHotkey(); handled = true; }
                    return IntPtr.Zero;
                });
            }
            hotkeyRegistered = HotkeyOk = RegisterHotKey(hotkeySource.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 'H');     // Alt | Ctrl | no auto-repeat
        }
        catch (Exception ex) { Log(ex); HotkeyOk = false; }
    }

    public static readonly RotatingLog ErrorLog = new(Path.Combine(Paths.Dir, "error.log"), 512 * 1024);

    static void Log(Exception? e) => ErrorLog.Write(e);

    static DateTime lastNotice = DateTime.MinValue;

    /// <summary>Tells the user something failed (at most once a minute, so a repeating fault can't bury them in dialogs).</summary>
    static void Tell(Exception e)
    {
        if (DateTime.UtcNow - lastNotice < TimeSpan.FromMinutes(1)) return;
        lastNotice = DateTime.UtcNow;
        MessageBox.Show($"Something went wrong, but Hushwire is still running.\n\n{e.Message}\n\nDetails: About & diagnostics → Save report.", "Hushwire", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Shows the dashboard, recreating the window if it was ever closed for real.</summary>
    void ShowMain()
    {
        try
        {
            if (prot is null) return;
            if (win is null || win.Dead) win = new MainWindow(prot);
            win.Show("dash");
        }
        catch (Exception ex) { Log(ex); }
    }

    void BuildTray()
    {
        statusItem = new Forms.ToolStripMenuItem("Hushwire") { Enabled = false };
        toggleItem = new Forms.ToolStripMenuItem("Turn on", null, async (_, _) =>
        {
            if (prot!.State == ProtectionState.Paused) prot.Resume();
            else if (prot.State == ProtectionState.On) await prot.DisableAsync();
            else await prot.EnableAsync();
        });
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(toggleItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Pause for 30 minutes", null, (_, _) => prot!.Pause(TimeSpan.FromMinutes(30))));
        menu.Items.Add(new Forms.ToolStripMenuItem("Pause until I restart", null, (_, _) => prot!.PauseUntilRestart()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Something broke…", null, (_, _) => { ShowMain(); win?.OpenFixer(); }));
        menu.Items.Add(new Forms.ToolStripMenuItem("Open dashboard", null, (_, _) => ShowMain()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Update filter lists", null, async (_, _) => await prot!.UpdateListsAsync(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit (turns protection off)", null, (_, _) => Quit()));
        tray = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Hushwire" };
        tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMain(); };
    }

    void UpdateTray()
    {
        if (tray is null || prot is null) return;
        var st = prot.State;
        statusItem!.Text = st switch { ProtectionState.On => "Protection is on", ProtectionState.Paused => "Paused", ProtectionState.Starting => "Starting…", ProtectionState.Error => "Problem — open Hushwire", _ => "Protection is off" };
        toggleItem!.Text = st switch { ProtectionState.Paused => "Resume", ProtectionState.On => "Turn off", _ => "Turn on" };
        tray.Text = "Hushwire — " + statusItem.Text;
        if (st == lastTrayState) return;
        lastTrayState = st;
        var old = tray.Icon;
        tray.Icon = IconFactory.Make(st switch
        {
            ProtectionState.On => System.Windows.Media.Color.FromRgb(0x3E, 0xCF, 0xB2),
            ProtectionState.Paused => System.Windows.Media.Color.FromRgb(0xF5, 0xB5, 0x4A),
            ProtectionState.Error => System.Windows.Media.Color.FromRgb(0xF0, 0x61, 0x6D),
            _ => System.Windows.Media.Color.FromRgb(0x83, 0x93, 0xAD),
        });
        old?.Dispose();
    }

    void Quit()
    {
        if (prot?.State is ProtectionState.On or ProtectionState.Paused && !prot.Dev)
        {
            var r = MessageBox.Show("Closing Hushwire turns protection off and restores your normal DNS settings.\nProtection comes back automatically next time you start Hushwire.\n\nExit now?",
                "Exit Hushwire", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        QuitNow();
    }

    /// <summary>Exit without asking: hide at once, restore DNS off the UI thread, then close.</summary>
    async void QuitNow()
    {
        if (Quitting) return;
        Quitting = true;
        // Disappear immediately, then undo the network changes off the UI thread: the window never looks frozen.
        try { win?.Hide(); if (tray is not null) tray.Visible = false; } catch (Exception ex) { Log(ex); }
        try { await Task.Run(() => prot?.ShutdownRestore()); } catch (Exception ex) { Log(ex); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Quitting = true;
        if (!ReferenceEquals(prot, null) && !prot.IsShutDown) Log(new InvalidOperationException("exiting without the Exit command (session end or crash path)"));
        prot?.ShutdownRestore();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        base.OnExit(e);
    }
}
