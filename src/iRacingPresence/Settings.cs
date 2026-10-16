using System.Text.Json;

namespace IRacingPresence;

sealed class Settings
{
    /// Bumped whenever defaults change, so older settings files get upgraded once.
    public const int CurrentVersion = 3;

    /// The shared "iRacing" Discord application, so the app works without any setup.
    public const string DefaultApplicationId = "1560590131432398900";

    /// 0 when the file has no "version" (written before 1.3.0); Save stamps the current one.
    public int Version { get; set; }
    public string DiscordApplicationId { get; set; } = DefaultApplicationId;
    public double UpdateIntervalSeconds { get; set; } = 2;

    public string ActivityType { get; set; } = "playing";
    public string MemberListShows { get; set; } = "name";

    public string Details { get; set; } = "{series} · {session} · {moment} · {progress} · {standing}";
    public string State { get; set; } = "{car} @ {track}";
    public string SpectateState { get; set; } = "{watching} @ {track}";
    public string LargeImage { get; set; } = "{car_image}|iracing";
    public string LargeImageText { get; set; } = "{car} · {team} · {rating} · {license}";
    public string SmallImage { get; set; } = "{track_image}";
    public string SmallImageText { get; set; } = "{track_full} · {conditions} · {best}";
    public string TemperatureUnit { get; set; } = "C";

    public bool ShowTimeLeft { get; set; } = true;
    public bool ShowElapsedTime { get; set; } = true;
    public bool ShowPosition { get; set; } = false;

    public IdleSettings Idle { get; set; } = new();
    public string ResultsWebhookUrl { get; set; } = "";
    public bool SharedNames { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public List<ButtonSettings> Buttons { get; set; } = [];
    public List<string> IRacingProcesses { get; set; } = ["iRacingUI", "iRacingSim64DX11"];

    public bool HasApplicationId => DiscordApplicationId.Length > 0 && DiscordApplicationId.All(char.IsAsciiDigit);
    public bool UsesFahrenheit => TemperatureUnit.Trim().StartsWith("F", StringComparison.OrdinalIgnoreCase);
    public bool PostsResults => Uri.TryCreate(ResultsWebhookUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http";
}

sealed class IdleSettings
{
    public bool Enabled { get; set; } = true;
    public string Details { get; set; } = "In the menus";
    public string State { get; set; } = "";
}

sealed class ButtonSettings
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

/// Loads settings.json and reloads it whenever the file changes, so edits apply without a restart.
/// Saving rewrites the whole file from a commented template.
sealed class SettingsStore(string path)
{
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly object _gate = new();
    Settings _current = new();
    DateTime _stamp = DateTime.MinValue;

    public string Path { get; } = path;
    public string? LoadError { get; private set; }
    public bool CreatedThisRun { get; private set; }

    public Settings Current
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(Path))
                    {
                        Save(new Settings());
                        CreatedThisRun = true;
                        Log.Info($"Created {Path}");
                    }
                    var stamp = File.GetLastWriteTimeUtc(Path);
                    if (stamp != _stamp)
                    {
                        _stamp = stamp;
                        var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), ReadOptions) ?? new Settings();
                        if (string.IsNullOrWhiteSpace(loaded.DiscordApplicationId))
                            loaded.DiscordApplicationId = Settings.DefaultApplicationId;
                        if (loaded.Version < Settings.CurrentVersion)
                        {
                            Upgrade(loaded);
                            Save(loaded);
                            Log.Info("Updated settings.json to this version's defaults (race moments, stats and conditions).");
                        }
                        _current = loaded;
                        LoadError = null;
                        Log.Info("Loaded settings.json");
                    }
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                {
                    if (LoadError == null)
                        Log.Error($"settings.json is invalid, keeping the previous settings: {e.Message}");
                    LoadError = e.Message;
                }
                return _current;
            }
        }
    }

    public void Save(Settings settings)
    {
        lock (_gate)
        {
            settings.Version = Settings.CurrentVersion;
            File.WriteAllText(Path, Render(settings));
            _stamp = File.GetLastWriteTimeUtc(Path);
            _current = Clone(settings);
            LoadError = null;
        }
    }

    public static Settings Clone(Settings s) =>
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, WriteOptions), ReadOptions)!;

    /// Older files keep the defaults they were written with; swap the untouched ones for the
    /// current defaults. Customised values are never changed.
    static void Upgrade(Settings s)
    {
        var defaults = new Settings();
        // details: 1.1.0, 1.1.1 and 1.2.0 defaults
        if (s.Details is "{series} · {session} · {progress}" or "{series} · {session} · {progress} · {players}")
            s.ShowPosition = false; // the position moved onto the top line in 1.2.0
        if (s.Details is "{series} · {session} · {progress}" or "{series} · {session} · {progress} · {players}"
            or "{series} · {session} · {progress} · {standing}")
            s.Details = defaults.Details;
        if (s.LargeImageText == "{car}")
            s.LargeImageText = defaults.LargeImageText;
        if (s.SmallImageText == "{track_full}")
            s.SmallImageText = defaults.SmallImageText;
    }

    static string Render(Settings s)
    {
        var values = new Dictionary<string, object?>
        {
            ["version"] = s.Version,
            ["discordApplicationId"] = s.DiscordApplicationId,
            ["updateIntervalSeconds"] = s.UpdateIntervalSeconds,
            ["activityType"] = s.ActivityType,
            ["memberListShows"] = s.MemberListShows,
            ["details"] = s.Details,
            ["state"] = s.State,
            ["spectateState"] = s.SpectateState,
            ["largeImage"] = s.LargeImage,
            ["largeImageText"] = s.LargeImageText,
            ["smallImage"] = s.SmallImage,
            ["smallImageText"] = s.SmallImageText,
            ["temperatureUnit"] = s.TemperatureUnit,
            ["showTimeLeft"] = s.ShowTimeLeft,
            ["showElapsedTime"] = s.ShowElapsedTime,
            ["showPosition"] = s.ShowPosition,
            ["idle.enabled"] = s.Idle.Enabled,
            ["idle.details"] = s.Idle.Details,
            ["idle.state"] = s.Idle.State,
            ["resultsWebhookUrl"] = s.ResultsWebhookUrl,
            ["sharedNames"] = s.SharedNames,
            ["checkForUpdates"] = s.CheckForUpdates,
            ["buttons"] = s.Buttons,
            ["iRacingProcesses"] = s.IRacingProcesses,
        };
        var text = Template;
        foreach (var (key, value) in values)
            text = text.Replace($"@@{key}@@", JsonSerializer.Serialize(value, WriteOptions));
        return text;
    }

    const string Template = """
        {
          // iRacing Discord Presence settings. The easy way to change them: right-click the "iR" tray
          // icon › Settings. Editing this file works too; changes apply as soon as you save it.
          "version": @@version@@,

          // The Discord application your status is shown through. Its name ("iRacing") is what
          // appears after "Playing". To use your own, create one at
          // https://discord.com/developers/applications and paste its Application ID here.
          "discordApplicationId": @@discordApplicationId@@,

          // How often to read iRacing, in seconds. Discord itself only accepts an update every few seconds.
          "updateIntervalSeconds": @@updateIntervalSeconds@@,

          // "playing" or "competing" ("Competing in iRacing"). Falls back to playing if Discord refuses.
          "activityType": @@activityType@@,
          // What friends see next to your name in the member list: "name" (iRacing), "details" or "state".
          "memberListShows": @@memberListShows@@,

          // Text lines. Placeholders:
          //   {series} {session} {moment} ("Last lap", "Caution", "🏁 Finished", "In the pits", ...)
          //   {standing} ("P3 of 24", or "24 drivers" before you have a position) {players} ("24 drivers")
          //   {progress} ("Lap 7/20" in lap races) {track} {track_full} {track_config} {track_country}
          //   {car} {car_short} {car_class} {car_number} {team} {driver} {rating} ("2450 iR") {license}
          //   {best} ("Best 2:17.512") {conditions} ("Rain · Night · 14°C") {watching} ("Watching Max")
          //   {watching_car} {position} {class_position} {field_size} {class_field_size} {lap}
          //   {laps_total} {laps} {time_left} {incidents} {best_lap} {irating} {status}
          // Empty placeholders and their " · " / " @ " / " | " separators are removed automatically.
          "details": @@details@@,
          "state": @@state@@,
          // The second line while you're spectating someone else.
          "spectateState": @@spectateState@@,

          // Pictures. {car_image} and {track_image} are the official pictures of your current car and
          // track from iracing.com; they change by themselves whenever your car or track changes.
          // You can also use the name of an art asset uploaded to your Discord app
          // (Rich Presence > Art Assets) or a direct https:// link. "a|b" means "a, or b if a is empty".
          // The large image is the big picture; the small one is the round badge in its corner.
          // The "Text" lines are what people see when they hover over each picture.
          "largeImage": @@largeImage@@,
          "largeImageText": @@largeImageText@@,
          "smallImage": @@smallImage@@,
          "smallImageText": @@smallImageText@@,
          // "C" or "F" for the temperature in {conditions}.
          "temperatureUnit": @@temperatureUnit@@,

          // Countdown ("12:34 left") for timed sessions.
          "showTimeLeft": @@showTimeLeft@@,
          // Elapsed timer when the session has no time limit (lap races, open practice).
          "showElapsedTime": @@showElapsedTime@@,
          // Discord's own "(3 of 24)" after the second line. Off because the top line already
          // shows your position ({standing}).
          "showPosition": @@showPosition@@,

          // Shown while the iRacing UI is open but you're not in a session.
          "idle": {
            "enabled": @@idle.enabled@@,
            "details": @@idle.details@@,
            "state": @@idle.state@@
          },

          // Post your race results to a Discord channel. In Discord: Server Settings › Integrations ›
          // Webhooks › New Webhook › Copy Webhook URL, and paste it here. Leave empty to turn off.
          "resultsWebhookUrl": @@resultsWebhookUrl@@,

          // Use the series names everyone shares on GitHub when you haven't named a series yourself.
          "sharedNames": @@sharedNames@@,
          // Check GitHub for new versions and offer to install them.
          "checkForUpdates": @@checkForUpdates@@,

          // Up to two link buttons (only other people can see them), e.g.
          // [ { "label": "My iRacing profile", "url": "https://members-ng.iracing.com/" } ]
          "buttons": @@buttons@@,

          // Process names that count as "iRacing is open" for the idle status.
          "iRacingProcesses": @@iRacingProcesses@@
        }

        """;
}
