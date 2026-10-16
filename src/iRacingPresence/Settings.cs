using System.Text.Json;
using System.Text.RegularExpressions;

namespace IRacingPresence;

sealed class Settings
{
    /// The shared "iRacing" Discord application, so the app works without any setup.
    public const string DefaultApplicationId = "1560590131432398900";

    public string DiscordApplicationId { get; set; } = DefaultApplicationId;
    public double UpdateIntervalSeconds { get; set; } = 2;

    public string ActivityType { get; set; } = "playing";
    public string MemberListShows { get; set; } = "name";

    public string Details { get; set; } = "{series} · {session} · {progress} · {standing}";
    public string State { get; set; } = "{car} @ {track}";
    public string LargeImage { get; set; } = "{car_image}|iracing";
    public string LargeImageText { get; set; } = "{car}";
    public string SmallImage { get; set; } = "{track_image}";
    public string SmallImageText { get; set; } = "{track_full}";

    public bool ShowTimeLeft { get; set; } = true;
    public bool ShowElapsedTime { get; set; } = true;
    public bool ShowPosition { get; set; } = false;

    public IdleSettings Idle { get; set; } = new();
    public List<ButtonSettings> Buttons { get; set; } = [];
    public List<string> IRacingProcesses { get; set; } = ["iRacingUI", "iRacingSim64DX11"];

    public bool HasApplicationId => DiscordApplicationId.Length > 0 && DiscordApplicationId.All(char.IsAsciiDigit);
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
sealed partial class SettingsStore(string path)
{
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    Settings _current = new();
    DateTime _stamp = DateTime.MinValue;

    public string Path { get; } = path;
    public string? LoadError { get; private set; }
    public bool CreatedThisRun { get; private set; }

    public Settings Current
    {
        get
        {
            try
            {
                if (!File.Exists(Path))
                {
                    File.WriteAllText(Path, DefaultFile);
                    CreatedThisRun = true;
                    Log.Info($"Created {Path}");
                }
                var stamp = File.GetLastWriteTimeUtc(Path);
                if (stamp != _stamp)
                {
                    var text = File.ReadAllText(Path);
                    var upgraded = UpgradeOldDefaults(text);
                    if (upgraded != text)
                    {
                        File.WriteAllText(Path, upgraded);
                        text = upgraded;
                        stamp = File.GetLastWriteTimeUtc(Path);
                        Log.Info("Updated settings.json to the new defaults (the top line now shows your position, or the number of drivers).");
                    }
                    _stamp = stamp;
                    _current = JsonSerializer.Deserialize<Settings>(text, ReadOptions) ?? new Settings();
                    if (string.IsNullOrWhiteSpace(_current.DiscordApplicationId))
                        _current.DiscordApplicationId = Settings.DefaultApplicationId;
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

    /// Settings files written by older versions keep their old defaults; swap those for the
    /// current ones. Only exact old default values are touched, never customised ones.
    static string UpgradeOldDefaults(string text)
    {
        // 1.2.0: position (or the driver count) on the top line
        const string details = "\"details\": \"{series} · {session} · {progress} · {standing}\"";
        text = text
            .Replace("\"details\": \"{series} · {session} · {progress}\"", details)               // from 1.1.0
            .Replace("\"details\": \"{series} · {session} · {progress} · {players}\"", details);  // from 1.1.1
        // ...so Discord's own "(3 of 24)" would repeat it.
        return OldShowPositionDefault().Replace(text,
            "$1// Discord's own \"(3 of 24)\" after the second line. Off because the top line already$2\n" +
            "$1// shows your position ({standing}).$2\n" +
            "$3\"showPosition\": false");
    }

    [GeneratedRegex("""^([ \t]*)// Position as "\(3 of 24\)"\. Uses your class position in multiclass races\.(\r?)\n([ \t]*)"showPosition": true""", RegexOptions.Multiline)]
    private static partial Regex OldShowPositionDefault();

    const string DefaultFile = """
        {
          // The Discord application your status is shown through. Its name ("iRacing") is what
          // appears after "Playing". To use your own, create one at
          // https://discord.com/developers/applications and paste its Application ID here.
          "discordApplicationId": "1560590131432398900",

          // How often to read iRacing, in seconds. Discord itself only accepts an update every few seconds.
          "updateIntervalSeconds": 2,

          // "playing" or "competing" ("Competing in iRacing"). Falls back to playing if Discord refuses.
          "activityType": "playing",
          // What friends see next to your name in the member list: "name" (iRacing), "details" or "state".
          "memberListShows": "name",

          // Text lines. Placeholders:
          //   {series} {session} {track} {track_full} {track_config} {track_country}
          //   {car} {car_short} {car_class} {car_number} {status}
          //   {standing} ("P3 of 24", or "24 drivers" before you have a position) {players} ("24 drivers")
          //   {position} {class_position} {field_size} {class_field_size}
          //   {lap} {laps_total} {laps} {progress} {time_left} {incidents} {best_lap} {irating} {license}
          // Empty placeholders and their " · " / " @ " / " | " separators are removed automatically.
          "details": "{series} · {session} · {progress} · {standing}",
          "state": "{car} @ {track}",

          // Pictures. {car_image} and {track_image} are the official pictures of your current car and
          // track from iracing.com; they change by themselves whenever your car or track changes.
          // You can also use the name of an art asset uploaded to your Discord app
          // (Rich Presence > Art Assets) or a direct https:// link. "a|b" means "a, or b if a is empty".
          // The large image is the big picture; the small one is the round badge in its corner.
          // Swap them to make the track the big picture.
          "largeImage": "{car_image}|iracing",
          "largeImageText": "{car}",
          "smallImage": "{track_image}",
          "smallImageText": "{track_full}",

          // Countdown ("12:34 left") for timed sessions.
          "showTimeLeft": true,
          // Elapsed timer when the session has no time limit (lap races, open practice).
          "showElapsedTime": true,
          // Discord's own "(3 of 24)" after the second line. Off because the top line already
          // shows your position ({standing}).
          "showPosition": false,

          // Shown while the iRacing UI is open but you're not in a session.
          "idle": {
            "enabled": true,
            "details": "In the menus",
            "state": ""
          },

          // Up to two link buttons (only other people can see them), e.g.
          // { "label": "My iRacing profile", "url": "https://members-ng.iracing.com/" }
          "buttons": [],

          // Process names that count as "iRacing is open" for the idle status.
          "iRacingProcesses": [ "iRacingUI", "iRacingSim64DX11" ]
        }
        """;
}
