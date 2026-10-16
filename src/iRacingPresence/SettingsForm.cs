using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using IRacingPresence.Discord;
using IRacingPresence.IRacing;

namespace IRacingPresence;

/// The settings window: every option on three tabs, with a live preview of the Discord card
/// built by the same code that builds the real status.
sealed class SettingsForm : Form
{
    readonly SettingsStore _store;
    readonly ImageCatalog _images;
    readonly Func<(SimState? Sim, string? Series)> _live;
    readonly Settings _original;
    readonly bool _startupWasOn = Installer.StartupEnabled;

    readonly TextBox _details = Field(), _state = Field(), _spectate = Field(), _idleDetails = Field(), _idleState = Field();
    readonly CheckBox _idleEnabled = Check("Show a status while you're in the iRacing menus");
    readonly CheckBox _timeLeft = Check("Countdown in timed sessions (\"12:34 left\")");
    readonly CheckBox _elapsed = Check("Elapsed timer when there's no time limit");
    readonly CheckBox _party = Check("Also show Discord's own \"(3 of 24)\" after the second line");
    readonly ComboBox _activityType = Combo("playing", "competing");
    readonly ComboBox _memberList = Combo("name", "details", "state");

    readonly TextBox _largeImage = Field(), _largeText = Field(), _smallImage = Field(), _smallText = Field();
    readonly ComboBox _tempUnit = Combo("C", "F");

    readonly TextBox _webhook = Field();
    readonly Button _testWebhook = new() { Text = "Send test message", AutoSize = true };
    readonly Label _webhookResult = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) };
    readonly CheckBox _sharedNames = Check("Use series names shared by everyone on GitHub");
    readonly CheckBox _updates = Check("Check for updates and offer to install them");
    readonly CheckBox _startup = Check("Start with Windows");

    readonly ComboBox _previewSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly List<Func<Settings, (SimState? Sim, string Series)>> _previewStates = [];
    readonly ActivityCard _card = new() { Dock = DockStyle.Fill };
    readonly ListBox _placeholders = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Consolas", 9f) };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    TextBox _lastTemplate;
    bool _loading;

    public SettingsForm(SettingsStore store, ImageCatalog images, Func<(SimState? Sim, string? Series)> live)
    {
        _store = store;
        _images = images;
        _live = live;
        _original = SettingsStore.Clone(store.Current);
        _lastTemplate = _details;

        Text = $"{Installer.AppName} {Installer.Version} · Settings";
        Font = new Font("Segoe UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception e) when (e is ArgumentException or FileNotFoundException) { }

        var tabs = _tabs;
        tabs.TabPages.Add(Page("Status",
            ("Top line", _details),
            ("Second line", _state),
            ("While spectating", _spectate),
            (null, Hint("Placeholders like {series} or {standing} are filled in live; empty ones disappear with their \" · \".")),
            (null, _timeLeft),
            (null, _elapsed),
            (null, _party),
            ("Shown as", _activityType),
            ("Member list shows", _memberList),
            (null, Hint("\"name\" shows \"Playing iRacing\" next to your name; \"details\" shows your top line there instead.")),
            (null, _idleEnabled),
            ("Menus top line", _idleDetails),
            ("Menus second line", _idleState)));
        tabs.TabPages.Add(Page("Pictures",
            ("Big picture", _largeImage),
            ("Big picture hover", _largeText),
            ("Badge", _smallImage),
            ("Badge hover", _smallText),
            (null, Hint("{car_image} and {track_image} are the official iracing.com pictures of your car and track. " +
                        "You can also use a Discord art asset name or an https:// link. \"a|b\" means \"a, or b if a is empty\".")),
            ("Temperature", _tempUnit)));
        var webhookRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        webhookRow.Controls.Add(_testWebhook);
        webhookRow.Controls.Add(_webhookResult);
        var repoLink = new LinkLabel { Text = AppInfo.RepoUrl, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        repoLink.LinkClicked += (_, _) => Open(AppInfo.RepoUrl);
        tabs.TabPages.Add(Page("Extras",
            ("Results webhook", _webhook),
            (null, webhookRow),
            (null, Hint("Posts \"finished P3 of 24\" with your best lap and incidents to a Discord channel after each race. " +
                        "Create one in Discord: Server Settings › Integrations › Webhooks › New Webhook › Copy Webhook URL.")),
            (null, _sharedNames),
            (null, _updates),
            (null, _startup),
            (null, repoLink)));

        var preview = new GroupBox { Text = "Preview", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var previewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.Controls.Add(_previewSource, 0, 0);
        previewLayout.Controls.Add(_card, 0, 1);
        previewLayout.Controls.Add(new Label { Text = "Placeholders (double-click to insert into the last box you clicked)", AutoSize = true, Padding = new Padding(0, 8, 0, 4) }, 0, 2);
        previewLayout.Controls.Add(_placeholders, 0, 3);
        preview.Controls.Add(previewLayout);

        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Reset to defaults", AutoSize = true };
        var openFile = new Button { Text = "Open settings file", AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([cancel, save, reset, openFile]);
        AcceptButton = save;
        CancelButton = cancel;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(preview, 1, 0);
        root.Controls.Add(buttons, 0, 1);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);

        foreach (var box in new[] { _details, _state, _spectate, _idleDetails, _idleState, _largeImage, _largeText, _smallImage, _smallText, _webhook })
        {
            box.TextChanged += (_, _) => UpdatePreview(false);
            if (box != _webhook)
                box.Enter += (_, _) => _lastTemplate = box;
        }
        foreach (var check in new[] { _idleEnabled, _timeLeft, _elapsed, _party, _sharedNames, _updates, _startup })
            check.CheckedChanged += (_, _) => UpdatePreview(false);
        foreach (var combo in new[] { _activityType, _memberList, _tempUnit })
            combo.SelectedIndexChanged += (_, _) => UpdatePreview(false);
        _previewSource.SelectedIndexChanged += (_, _) => UpdatePreview(true);
        _placeholders.DoubleClick += (_, _) => InsertPlaceholder();
        _testWebhook.Click += async (_, _) => await TestWebhook();
        save.Click += (_, _) => SaveAndClose();
        reset.Click += (_, _) => ResetToDefaults();
        openFile.Click += (_, _) => Open("notepad.exe", _store.Path);
        _tick.Tick += (_, _) => UpdatePreview(false);
        FormClosed += (_, _) => _tick.Stop();

        BuildPreviewChoices();
        LoadIntoUi(_original);
        _startup.Checked = _startupWasOn;
        _tick.Start();
    }

    /// For checking the layout without a screen: shows the window off-screen, waits for the
    /// preview pictures to load, and saves each tab as PREFIX-0.png, PREFIX-1.png, ...
    public void RenderTabs(string prefix)
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-4000, -4000);
        ShowInTaskbar = false;
        Show();
        void Pump(double seconds)
        {
            var until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
        }
        Pump(6);
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            _tabs.SelectedIndex = i;
            Pump(0.5);
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save($"{prefix}-{i}.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        Close();
    }

    void BuildPreviewChoices()
    {
        var (liveSim, liveSeries) = _live();
        if (liveSim != null)
        {
            _previewSource.Items.Add("Your current session");
            _previewStates.Add(_ => (_live().Sim ?? liveSim, _live().Series ?? liveSeries ?? liveSim.FallbackSeriesName));
        }
        foreach (var (name, state) in DemoStates.Samples)
        {
            _previewSource.Items.Add($"Example: {name}");
            _previewStates.Add(cfg => (WithPictures(state, cfg), state.KnownSeriesName ?? state.FallbackSeriesName));
        }
        _previewSource.Items.Add("In the iRacing menus");
        _previewStates.Add(_ => (null, ""));
        _previewSource.SelectedIndex = liveSim != null ? 0 : 2; // live session, else the example race
    }

    SimState WithPictures(SimState s, Settings cfg) =>
        PresenceBuilder.UsesPictures(cfg) ? s with { CarImage = _images.Car(s.Car), TrackImage = _images.Track(s.TrackFull) } : s;

    void UpdatePreview(bool placeholdersToo)
    {
        if (_loading || _previewSource.SelectedIndex < 0)
            return;
        var cfg = FromUi();
        var (sim, series) = _previewStates[_previewSource.SelectedIndex](cfg);
        var now = DateTimeOffset.Now;
        var activity = sim == null
            ? cfg.Idle.Enabled ? PresenceBuilder.BuildIdle(cfg, now.AddMinutes(-4)) : null
            : PresenceBuilder.Build(sim, series, cfg, now);
        _card.ShowActivity(activity, now);

        // Refill the list only when a value changed (e.g. a picture finished loading), keeping the
        // scroll position, so it doesn't flicker every second.
        var values = sim == null ? new Dictionary<string, string>() : PresenceBuilder.Placeholders(sim, series, cfg.UsesFahrenheit);
        var lines = values.OrderBy(v => v.Key)
            .Select(v => $"{{{v.Key}}}".PadRight(20) + (v.Value.Length > 0 ? v.Value : "(empty here)"))
            .ToList();
        if (placeholdersToo || !lines.SequenceEqual(_placeholders.Items.Cast<string>()))
        {
            int top = placeholdersToo ? 0 : _placeholders.TopIndex, selected = _placeholders.SelectedIndex;
            _placeholders.BeginUpdate();
            _placeholders.Items.Clear();
            _placeholders.Items.AddRange([.. lines]);
            if (!placeholdersToo && selected < lines.Count)
                _placeholders.SelectedIndex = selected;
            _placeholders.TopIndex = Math.Min(top, Math.Max(0, lines.Count - 1));
            _placeholders.EndUpdate();
        }
    }

    void InsertPlaceholder()
    {
        if (_placeholders.SelectedItem is not string line)
            return;
        var token = line.Split(' ', 2)[0];
        _lastTemplate.Focus();
        _lastTemplate.SelectedText = token;
    }

    async Task TestWebhook()
    {
        var url = _webhook.Text.Trim();
        if (url.Length == 0)
        {
            _webhookResult.Text = "Paste a webhook URL first.";
            return;
        }
        _testWebhook.Enabled = false;
        _webhookResult.Text = "Sending…";
        var error = await ResultsReporter.SendTestAsync(url);
        _webhookResult.Text = error == null ? "Sent! Check your channel." : $"Didn't work: {error}";
        _testWebhook.Enabled = true;
    }

    void LoadIntoUi(Settings s)
    {
        _loading = true;
        _details.Text = s.Details;
        _state.Text = s.State;
        _spectate.Text = s.SpectateState;
        _idleEnabled.Checked = s.Idle.Enabled;
        _idleDetails.Text = s.Idle.Details;
        _idleState.Text = s.Idle.State;
        _timeLeft.Checked = s.ShowTimeLeft;
        _elapsed.Checked = s.ShowElapsedTime;
        _party.Checked = s.ShowPosition;
        SelectItem(_activityType, s.ActivityType);
        SelectItem(_memberList, s.MemberListShows);
        _largeImage.Text = s.LargeImage;
        _largeText.Text = s.LargeImageText;
        _smallImage.Text = s.SmallImage;
        _smallText.Text = s.SmallImageText;
        SelectItem(_tempUnit, s.UsesFahrenheit ? "F" : "C");
        _webhook.Text = s.ResultsWebhookUrl;
        _sharedNames.Checked = s.SharedNames;
        _updates.Checked = s.CheckForUpdates;
        _loading = false;
        UpdatePreview(true);
    }

    Settings FromUi()
    {
        var s = SettingsStore.Clone(_original);
        s.Details = _details.Text;
        s.State = _state.Text;
        s.SpectateState = _spectate.Text;
        s.Idle.Enabled = _idleEnabled.Checked;
        s.Idle.Details = _idleDetails.Text;
        s.Idle.State = _idleState.Text;
        s.ShowTimeLeft = _timeLeft.Checked;
        s.ShowElapsedTime = _elapsed.Checked;
        s.ShowPosition = _party.Checked;
        s.ActivityType = _activityType.Text;
        s.MemberListShows = _memberList.Text;
        s.LargeImage = _largeImage.Text;
        s.LargeImageText = _largeText.Text;
        s.SmallImage = _smallImage.Text;
        s.SmallImageText = _smallText.Text;
        s.TemperatureUnit = _tempUnit.Text;
        s.ResultsWebhookUrl = _webhook.Text.Trim();
        s.SharedNames = _sharedNames.Checked;
        s.CheckForUpdates = _updates.Checked;
        return s;
    }

    void ResetToDefaults()
    {
        var defaults = new Settings { ResultsWebhookUrl = _webhook.Text.Trim() };
        LoadIntoUi(defaults);
    }

    void SaveAndClose()
    {
        try
        {
            _store.Save(FromUi());
            if (_startup.Checked != _startupWasOn)
                Installer.StartupEnabled = _startup.Checked;
            Log.Info("Settings saved from the settings window.");
            Close();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't save the settings:\n{e.Message}", Installer.AppName);
        }
    }

    static TabPage Page(string title, params (string? Label, Control Control)[] rows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(10, 12, 16, 10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, control) in rows)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label == null)
            {
                table.Controls.Add(control, 0, row);
                table.SetColumnSpan(control, 2);
            }
            else
            {
                table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 0, 8, 0) }, 0, row);
                table.Controls.Add(control, 1, row);
            }
        }
        // An empty last row takes the spare height, so the real rows keep their natural size.
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(new Panel { Size = Size.Empty, Margin = Padding.Empty }, 0, table.RowCount++);
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
        page.Controls.Add(table);
        return page;
    }

    static TextBox Field() => new() { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
    static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 8) };

    static ComboBox Combo(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Anchor = AnchorStyles.Left };
        combo.Items.AddRange(items);
        return combo;
    }

    static void SelectItem(ComboBox combo, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        int index = combo.Items.Cast<string>().ToList().FindIndex(i => i.Equals(v, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = combo.Items.Add(value.Trim());
        combo.SelectedIndex = index;
    }

    static void Open(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, args == null ? "" : $"\"{args}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(e.Message, Installer.AppName);
        }
    }
}

/// Draws an activity the way Discord's profile card does: big picture with the round badge,
/// app name, two text lines and the timer. Hovering the pictures shows their hover text.
sealed class ActivityCard : Control
{
    static readonly Dictionary<string, Image?> Images = [];
    static readonly HashSet<string> Loading = [];
    static readonly Color Background = Color.FromArgb(30, 31, 34), Card = Color.FromArgb(43, 45, 49);
    static readonly Color Bright = Color.FromArgb(242, 243, 245), Normal = Color.FromArgb(219, 222, 225), Muted = Color.FromArgb(148, 155, 164);

    readonly ToolTip _tip = new();
    DiscordActivity? _activity;
    DateTimeOffset _now;
    Rectangle _large, _small;
    string? _tipShown;

    public ActivityCard()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void ShowActivity(DiscordActivity? activity, DateTimeOffset now)
    {
        _activity = activity;
        _now = now;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * k);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Background);

        var card = new Rectangle(S(6), S(6), Width - S(12), Height - S(12));
        using (var path = Rounded(card, S(10)))
        using (var brush = new SolidBrush(Card))
            g.FillPath(brush, path);

        using var small = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var title = new Font("Segoe UI Semibold", 10.5f);
        using var body = new Font("Segoe UI", 9.5f);
        TextRenderer.DrawText(g, "PLAYING", small, new Point(card.X + S(14), card.Y + S(12)), Muted);

        if (_activity == null)
        {
            TextRenderer.DrawText(g, "Nothing is shown on Discord in this case.", body,
                new Rectangle(card.X + S(14), card.Y + S(40), card.Width - S(28), S(40)), Muted, TextFormatFlags.WordBreak);
            _large = _small = Rectangle.Empty;
            return;
        }

        var a = _activity;
        _large = new Rectangle(card.X + S(14), card.Y + S(36), S(84), S(84));
        DrawPicture(g, a.Assets?.LargeImage, _large, S(10), circle: false);
        _small = Rectangle.Empty;
        if (a.Assets?.SmallImage is { } badge)
        {
            int size = S(30);
            _small = new Rectangle(_large.Right - size + S(6), _large.Bottom - size + S(6), size, size);
            var ring = Rectangle.Inflate(_small, S(3), S(3));
            using (var brush = new SolidBrush(Card))
                g.FillEllipse(brush, ring);
            DrawPicture(g, badge, _small, 0, circle: true);
        }

        int x = _large.Right + S(14), w = card.Right - x - S(12), y = _large.Y;
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, "iRacing", title, new Rectangle(x, y, w, S(22)), Bright, flags);
        y += S(22);
        if (a.Details != null)
        {
            TextRenderer.DrawText(g, a.Details, body, new Rectangle(x, y, w, S(20)), Normal, flags);
            y += S(20);
        }
        var state = a.State;
        if (a.Party?.Size is [var cur, var max])
            state = $"{state} ({cur} of {max})";
        if (state != null)
        {
            TextRenderer.DrawText(g, state, body, new Rectangle(x, y, w, S(20)), Normal, flags);
            y += S(20);
        }
        if (Timer(a) is { } timer)
            TextRenderer.DrawText(g, timer, body, new Rectangle(x, y, w, S(20)), Color.FromArgb(35, 165, 90), flags);
    }

    string? Timer(DiscordActivity a)
    {
        static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        if (a.Timestamps?.End is long end)
        {
            var left = DateTimeOffset.FromUnixTimeMilliseconds(end) - _now;
            return $"{Clock(left < TimeSpan.Zero ? TimeSpan.Zero : left)} left";
        }
        if (a.Timestamps?.Start is long start)
            return $"{Clock(_now - DateTimeOffset.FromUnixTimeMilliseconds(start))} elapsed";
        return null;
    }

    void DrawPicture(Graphics g, string? key, Rectangle rect, int radius, bool circle)
    {
        using var clip = new GraphicsPath();
        if (circle) clip.AddEllipse(rect);
        else clip.AddPath(Rounded(rect, radius), false);
        var image = key != null && key.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? GetImage(key) : null;

        var saved = g.Save();
        g.SetClip(clip);
        if (image != null)
        {
            // Crop to fill, like Discord does.
            float scale = Math.Max((float)rect.Width / image.Width, (float)rect.Height / image.Height);
            float w = rect.Width / scale, h = rect.Height / scale;
            g.DrawImage(image, rect, new RectangleF((image.Width - w) / 2, (image.Height - h) / 2, w, h), GraphicsUnit.Pixel);
        }
        else
        {
            using var brush = new SolidBrush(Color.FromArgb(56, 58, 64));
            g.FillRectangle(brush, rect);
            if (!circle && key != null)
            {
                // An uploaded Discord art asset: there's no way to see it from here, so name it.
                using var font = new Font("Segoe UI", 8f);
                TextRenderer.DrawText(g, key.StartsWith("http") ? "loading…" : $"asset\n\"{key}\"", font, rect, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }
        g.Restore(saved);
    }

    Image? GetImage(string url)
    {
        lock (Images)
        {
            if (Images.TryGetValue(url, out var cached))
                return cached;
            if (!Loading.Add(url))
                return null;
        }
        Task.Run(async () =>
        {
            Image? image = null;
            try
            {
                var bytes = await AppInfo.Http.GetByteArrayAsync(url);
                image = Image.FromStream(new MemoryStream(bytes));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException) { }
            lock (Images)
            {
                Images[url] = image;
                Loading.Remove(url);
            }
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(Invalidate));
        });
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? text = _small.Contains(e.Location) ? _activity?.Assets?.SmallText
            : _large.Contains(e.Location) ? _activity?.Assets?.LargeText
            : null;
        if (text != _tipShown)
        {
            _tipShown = text;
            _tip.SetToolTip(this, text ?? "");
        }
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
