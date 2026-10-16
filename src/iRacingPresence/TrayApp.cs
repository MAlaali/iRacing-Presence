using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic;

namespace IRacingPresence;

/// The notification-area icon and its menu. All the work happens in PresenceService.
sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(20);
    static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    readonly SettingsStore _settings;
    readonly NameBook _names;
    readonly SharedNames _sharedNames;
    readonly ImageCatalog _images;
    readonly RunOptions _options;
    readonly PresenceService _service;
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    readonly SynchronizationContext _ui;
    readonly System.Windows.Forms.Timer _updateTimer = new();

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem, _detailsItem, _stateItem, _nameItem, _pauseItem, _startupItem, _updateItem;
    readonly Icon _iconIdle, _iconLive, _iconPaused, _iconProblem;
    ServiceStatus? _status;
    SettingsForm? _settingsForm;
    Updater.Release? _update;
    string? _announcedUpdate;
    bool _balloonIsUpdate;

    public TrayApp(SettingsStore settings, NameBook names, SharedNames sharedNames, ImageCatalog images, string logPath, RunOptions options)
    {
        _settings = settings;
        _names = names;
        _sharedNames = sharedNames;
        _images = images;
        _options = options;
        _service = new PresenceService(settings, names, sharedNames, images, options);
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
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = Installer.StartupEnabled };
        _updateItem = new ToolStripMenuItem("Update", null, (_, _) => StartUpdate()) { Visible = false, Font = BoldMenuFont() };
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()) { Font = BoldMenuFont() };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _detailsItem,
            _stateItem,
            new ToolStripSeparator(),
            _updateItem,
            settingsItem,
            _nameItem,
            _pauseItem,
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open series names", null, (_, _) => OpenInNotepad(_names.Path)),
            new ToolStripMenuItem("Open settings file", null, (_, _) => OpenInNotepad(_settings.Path)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenInNotepad(logPath)),
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Version {Installer.Version} · check for updates", null, (_, _) => CheckForUpdates(manual: true)),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        ]);

        _tray = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = Installer.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonIsUpdate)
                StartUpdate();
        };
        _tray.BalloonTipClosed += (_, _) => _balloonIsUpdate = false;

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _service.StatusChanged += s => _ui.Post(_ => Show(s), null);
        if (!dryRun)
        {
            _service.UnnamedSeriesSeen += (key, sim) => _ui.Post(_ => Balloon(
                "New series spotted",
                $"{Describe(key)} at {sim.Track} has no name yet. Right-click this icon → \"Name this series…\" so Discord can show it."), null);
        }

        // Touch the settings once on the UI thread so a fresh settings.json exists before the loop starts.
        var cfg = _settings.Current;
        if (options.Welcome)
        {
            Balloon($"{Installer.AppName} is running",
                "Your iRacing sessions will now show on your Discord profile. It starts with Windows; right-click the \"iR\" icon for options.");
        }
        else if (!dryRun && !cfg.HasApplicationId)
        {
            Balloon("One-time setup", "Paste your Discord Application ID into the settings. It applies right away.");
            if (_settings.CreatedThisRun)
                OpenSettings();
        }

        _updateTimer.Interval = (int)FirstUpdateCheck.TotalMilliseconds;
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = (int)UpdateCheckInterval.TotalMilliseconds;
            CheckForUpdates(manual: false);
        };
        _updateTimer.Start();

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

    void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _images, () => (_status?.Sim, _status?.SeriesName));
        _settingsForm.FormClosed += (_, _) => _startupItem.Checked = Installer.StartupEnabled;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    void NameCurrentSeries()
    {
        if (_status is not { NameKey: { } key, Sim: { } sim })
            return;
        var current = _names.Get(key) ?? _sharedNames.Get(key) ?? "";
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

    void ToggleStartup()
    {
        Installer.StartupEnabled = !Installer.StartupEnabled;
        _startupItem.Checked = Installer.StartupEnabled;
    }

    // --- Updates -----------------------------------------------------------------------------

    void CheckForUpdates(bool manual)
    {
        // Development copies and test runs don't update themselves.
        if (_options.Demo || _options.DryRun || !Installer.IsRunningFrom(Installer.Default))
        {
            if (manual)
                MessageBox.Show("Updates only apply to the installed copy (run the setup to install it).", Installer.AppName);
            return;
        }
        if (!manual && !_settings.Current.CheckForUpdates)
            return;

        Task.Run(async () =>
        {
            try
            {
                var release = await Updater.CheckAsync();
                _ui.Post(_ => UpdateChecked(release, manual), null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                Log.Warn($"Update check failed: {e.Message}");
                if (manual)
                    _ui.Post(_ => MessageBox.Show($"Couldn't check for updates:\n{e.Message}", Installer.AppName), null);
            }
        });
    }

    void UpdateChecked(Updater.Release? release, bool manual)
    {
        _update = release;
        _updateItem.Visible = release != null;
        if (release == null)
        {
            if (manual)
                Balloon("You're up to date", $"{Installer.AppName} {Installer.Version} is the latest version.");
            return;
        }
        _updateItem.Text = $"Update to {release.Version.ToString(3)}…";
        Log.Info($"Version {release.Version.ToString(3)} is available.");
        if (manual || _announcedUpdate != release.Tag)
        {
            _announcedUpdate = release.Tag;
            Balloon($"Version {release.Version.ToString(3)} is available", "Click here to update. It takes a few seconds and keeps your settings.");
            _balloonIsUpdate = true;
        }
    }

    void StartUpdate()
    {
        if (_update is not { } release)
            return;
        var size = release.Size >= 1 << 20 ? $"{release.Size / (1024.0 * 1024):0} MB" : $"{release.Size / 1024.0:0} KB";
        if (MessageBox.Show($"Download and install {Installer.AppName} {release.Version.ToString(3)} ({size}) now?\n\nYour settings are kept.",
                Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _updateItem.Enabled = false;
        _updateItem.Text = "Downloading update…";
        Task.Run(async () =>
        {
            try
            {
                var setup = await Updater.DownloadAsync(release);
                Log.Info($"Installing version {release.Version.ToString(3)}.");
                Updater.Install(setup); // closes this copy
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Log.Warn($"Update failed: {e.Message}");
                _ui.Post(_ =>
                {
                    _updateItem.Enabled = true;
                    _updateItem.Text = $"Update to {release.Version.ToString(3)}…";
                    MessageBox.Show($"The update didn't work:\n{e.Message}\n\nYou can also download it from {AppInfo.RepoUrl}/releases", Installer.AppName);
                }, null);
            }
        });
    }

    // -----------------------------------------------------------------------------------------

    void Balloon(string title, string text)
    {
        _balloonIsUpdate = false;
        _tray.ShowBalloonTip(10000, title, text, ToolTipIcon.Info);
    }

    static Font BoldMenuFont() => new(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold);

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
            MessageBox.Show($"Couldn't open {path}:\n{e.Message}", Installer.AppName);
        }
    }

    void Exit()
    {
        _updateTimer.Stop();
        _stop.Cancel();
        // Give the loop a moment to clear the presence before the process ends.
        _loop.Wait(TimeSpan.FromSeconds(3));
        _settingsForm?.Close();
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
