using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic;
using Microsoft.Win32;

namespace IRacingPresence;

/// The notification-area icon and its menu. All the work happens in PresenceService.
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "iRacingPresence";

    readonly SettingsStore _settings;
    readonly NameBook _names;
    readonly PresenceService _service;
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    readonly SynchronizationContext _ui;

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem, _detailsItem, _stateItem, _nameItem, _pauseItem, _startupItem;
    readonly Icon _iconIdle, _iconLive, _iconPaused, _iconProblem;
    ServiceStatus? _status;

    public TrayApp(SettingsStore settings, NameBook names, ImageCatalog images, string logPath, RunOptions options)
    {
        _settings = settings;
        _names = names;
        _service = new PresenceService(settings, names, images, options);
        bool dryRun = options.DryRun;

        _iconIdle = MakeIcon(Color.FromArgb(110, 118, 129));
        _iconLive = MakeIcon(Color.FromArgb(35, 134, 54));
        _iconPaused = MakeIcon(Color.FromArgb(56, 98, 196));
        _iconProblem = MakeIcon(Color.FromArgb(207, 34, 46));

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _detailsItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _stateItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _nameItem = new ToolStripMenuItem("Name this series…", null, (_, _) => NameCurrentSeries()) { Visible = false };
        _pauseItem = new ToolStripMenuItem("Hide my iRacing status", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = StartsWithWindows() };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _detailsItem,
            _stateItem,
            new ToolStripSeparator(),
            _nameItem,
            _pauseItem,
            new ToolStripMenuItem("Open settings", null, (_, _) => OpenInNotepad(_settings.Path)),
            new ToolStripMenuItem("Open series names", null, (_, _) => OpenInNotepad(_names.Path)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenInNotepad(logPath)),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Version {Installer.Version}") { Enabled = false },
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        ]);

        _tray = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "iRacing Discord Presence",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenInNotepad(_settings.Path);

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _service.StatusChanged += s => _ui.Post(_ => Show(s), null);
        if (!dryRun)
        {
            _service.UnnamedSeriesSeen += (key, sim) => _ui.Post(_ => _tray.ShowBalloonTip(8000,
                "New series spotted",
                $"{Describe(key)} at {sim.Track} has no name yet. Right-click this icon → \"Name this series…\" so Discord can show it.",
                ToolTipIcon.Info), null);
        }

        // Touch the settings once on the UI thread so a fresh settings.json exists before the loop starts.
        var cfg = _settings.Current;
        if (options.Welcome)
        {
            _tray.ShowBalloonTip(10000, $"{Installer.AppName} is running",
                "Your iRacing sessions will now show on your Discord profile. It starts with Windows; right-click the \"iR\" icon for options.",
                ToolTipIcon.Info);
        }
        else if (!dryRun && !cfg.HasApplicationId)
        {
            _tray.ShowBalloonTip(10000, "One-time setup",
                "Paste your Discord Application ID into settings.json and save. It applies right away.",
                ToolTipIcon.Info);
            if (_settings.CreatedThisRun)
                OpenInNotepad(_settings.Path);
        }

        _loop = Task.Run(() => _service.RunAsync(_stop.Token));
    }

    void Show(ServiceStatus s)
    {
        _status = s;

        string line = s.Paused ? "Hidden: your iRacing status isn't shown"
            : s.Problem != null ? s.Problem
            : s.InSession ? "Showing your session on Discord"
            : s.IRacingOpen ? "iRacing open: showing the menu status"
            : "Waiting for iRacing…";
        _statusItem.Text = Truncate(line, 90);
        _detailsItem.Text = s.Details ?? "";
        _detailsItem.Visible = s.Details != null;
        _stateItem.Text = s.State ?? "";
        _stateItem.Visible = s.State != null;

        _nameItem.Visible = s.NameKey != null;
        _nameItem.Text = s.NameKey == null ? "Name this series…" : $"Name this {(s.NameKey.StartsWith("league") ? "league" : "series")}… ({Describe(s.NameKey)})";
        _pauseItem.Checked = s.Paused;

        _tray.Icon = s.Paused ? _iconPaused
            : s.Problem != null ? _iconProblem
            : s.InSession || s.IRacingOpen ? _iconLive
            : _iconIdle;
        _tray.Text = Truncate(s.Details != null ? $"iRacing Presence: {s.Details}" : $"iRacing Presence: {line}", 127);
    }

    void NameCurrentSeries()
    {
        if (_status is not { NameKey: { } key, Sim: { } sim })
            return;
        var current = _names.Get(key) ?? "";
        var name = Interaction.InputBox(
            $"What should Discord call {Describe(key)}?\n\nSeen at {sim.TrackFull} with the {sim.Car}.\nExamples: GT Sprint, IMSA, GT3 Challenge.",
            "Name this series", current);
        if (name.Trim().Length > 0)
        {
            _names.Set(key, name);
            Log.Info($"Saved name for {key}: {name.Trim()}");
        }
    }

    void TogglePause()
    {
        _service.Paused = !_service.Paused;
        _pauseItem.Checked = _service.Paused;
        Log.Info(_service.Paused ? "Presence hidden by user." : "Presence shown again.");
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartsWithWindows())
            key.DeleteValue(RunValue, false);
        else
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        _startupItem.Checked = StartsWithWindows();
    }

    static void OpenInNotepad(string path)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "{}" : "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            MessageBox.Show($"Couldn't open {path}:\n{e.Message}", "iRacing Presence");
        }
    }

    void Exit()
    {
        _stop.Cancel();
        // Give the loop a moment to clear the presence before the process ends.
        _loop.Wait(TimeSpan.FromSeconds(3));
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    static string Describe(string key)
    {
        var parts = key.Split(':', 2);
        return parts.Length == 2 ? $"{parts[0]} ID {parts[1]}" : key;
    }

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    static Icon MakeIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("iR", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
