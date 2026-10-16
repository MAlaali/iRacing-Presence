using System.Globalization;
using System.Text.RegularExpressions;
using IRacingPresence.Discord;
using IRacingPresence.IRacing;

namespace IRacingPresence;

/// Turns a SimState into the Discord activity, using the templates from settings.json.
static partial class PresenceBuilder
{
    const int MaxText = 128;
    static readonly string[] Separators = [" · ", " | ", " @ ", " - "];

    public static DiscordActivity Build(SimState s, string seriesName, Settings cfg, DateTimeOffset now)
    {
        var values = Placeholders(s, seriesName);
        var activity = NewActivity(cfg, Fill(cfg.Details, values), Fill(cfg.State, values));
        activity.Assets = Assets(cfg, values);

        if (cfg.ShowTimeLeft && s.LapsTotal == null && s.TimeRemaining is double left)
            activity.Timestamps = new ActivityTimestamps { End = now.AddSeconds(left).ToUnixTimeMilliseconds() };
        else if (cfg.ShowElapsedTime)
            activity.Timestamps = new ActivityTimestamps { Start = now.AddSeconds(-s.SessionElapsed).ToUnixTimeMilliseconds() };

        var (position, size) = s.Multiclass ? (s.ClassPosition, s.ClassFieldSize) : (s.Position, s.FieldSize);
        if (cfg.ShowPosition && position > 0 && size >= position)
            activity.Party = new ActivityParty { Id = $"iracing-{s.SubSessionId}-{s.SessionNum}", Size = [position, size] };

        return activity;
    }

    public static DiscordActivity BuildIdle(Settings cfg, DateTimeOffset idleSince)
    {
        var values = new Dictionary<string, string>();
        var activity = NewActivity(cfg, Fill(cfg.Idle.Details, values), Fill(cfg.Idle.State, values));
        activity.Type = 0;
        activity.Assets = Assets(cfg, values);
        if (cfg.ShowElapsedTime)
            activity.Timestamps = new ActivityTimestamps { Start = idleSince.ToUnixTimeMilliseconds() };
        return activity;
    }

    public static Dictionary<string, string> Placeholders(SimState s, string seriesName)
    {
        var inv = CultureInfo.InvariantCulture;
        string laps = s.LapsTotal is int total ? $"Lap {Math.Min(Math.Max(s.Lap, 1), total)}/{total}"
            : s.Lap > 0 ? $"Lap {s.Lap}" : "";
        return new Dictionary<string, string>
        {
            ["series"] = seriesName,
            // A test drive is both the "series" and the session; say it once.
            ["session"] = s.Session.Equals(seriesName, StringComparison.OrdinalIgnoreCase) ? "" : s.Session,
            ["session_key"] = s.SessionKey,
            ["track"] = s.Track,
            ["track_full"] = s.TrackConfig.Length > 0 ? $"{s.TrackFull} - {s.TrackConfig}" : s.TrackFull,
            ["track_config"] = s.TrackConfig,
            ["track_country"] = s.TrackCountry,
            ["car"] = s.Car,
            ["car_short"] = s.CarShort,
            ["car_class"] = s.CarClass,
            ["car_number"] = s.CarNumber.Length > 0 ? $"#{s.CarNumber}" : "",
            ["car_image"] = s.CarImage ?? "",
            ["track_image"] = s.TrackImage ?? "",
            ["status"] = s.Status switch
            {
                DriverStatus.Driving => "On track",
                DriverStatus.InPits => "In the pits",
                DriverStatus.InGarage => "In the garage",
                DriverStatus.Finished => "Finished",
                _ => "Spectating",
            },
            ["position"] = s.Position > 0 ? s.Position.ToString(inv) : "",
            ["class_position"] = s.ClassPosition > 0 ? s.ClassPosition.ToString(inv) : "",
            ["field_size"] = s.FieldSize > 0 ? s.FieldSize.ToString(inv) : "",
            ["class_field_size"] = s.ClassFieldSize > 0 ? s.ClassFieldSize.ToString(inv) : "",
            ["lap"] = s.Lap > 0 ? s.Lap.ToString(inv) : "",
            ["laps_total"] = s.LapsTotal?.ToString(inv) ?? "",
            ["laps"] = laps,
            // Lap races have no useful countdown, so show the lap count instead.
            ["progress"] = s.LapsTotal != null && s.SessionKey == "race" ? laps : "",
            ["time_left"] = s.TimeRemaining is double left ? FormatTimeLeft(left) : "",
            ["incidents"] = $"{s.Incidents}x",
            ["best_lap"] = s.BestLap > 0 ? TimeSpan.FromSeconds(s.BestLap).ToString(@"m\:ss\.fff", inv) : "",
            ["irating"] = s.IRating is "" or "0" ? "" : s.IRating,
            ["license"] = s.License,
        };
    }

    static DiscordActivity NewActivity(Settings cfg, string? details, string? state) => new()
    {
        Type = cfg.ActivityType.Trim().ToLowerInvariant() switch
        {
            "competing" => 5,
            "watching" => 3,
            "listening" => 2,
            _ => 0,
        },
        StatusDisplayType = cfg.MemberListShows.Trim().ToLowerInvariant() switch
        {
            "state" => 1,
            "details" => 2,
            _ => null,
        },
        Details = details,
        State = state,
        Buttons = cfg.Buttons
            .Where(b => b.Label.Trim().Length > 0 && Uri.TryCreate(b.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            .Take(2)
            .Select(b => new ActivityButton { Label = Truncate(b.Label.Trim(), 32), Url = b.Url })
            .ToList() is { Count: > 0 } buttons ? buttons : null,
    };

    static ActivityAssets? Assets(Settings cfg, Dictionary<string, string> values)
    {
        var large = ImageKey(cfg.LargeImage, values);
        var small = ImageKey(cfg.SmallImage, values);
        if (large == null && small == null)
            return null;
        return new ActivityAssets
        {
            LargeImage = large,
            LargeText = large != null ? Fill(cfg.LargeImageText, values) : null,
            SmallImage = small,
            SmallText = small != null ? Fill(cfg.SmallImageText, values) : null,
        };
    }

    /// "a|b|c" uses the first option that isn't empty, e.g. "{car_image}|iracing" falls back to
    /// the uploaded asset when no car picture was found.
    static string? ImageKey(string template, Dictionary<string, string> values)
    {
        foreach (var option in template.Split('|'))
        {
            var key = PlaceholderPattern().Replace(option.Trim(), m => values.GetValueOrDefault(m.Groups[1].Value, "")).Trim();
            if (key.Length == 0)
                continue;
            // Uploaded asset names are lowercase; links must stay as typed.
            return key.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? key : key.ToLowerInvariant();
        }
        return null;
    }

    /// True when the image templates use iracing.com pictures, so they're only downloaded if wanted.
    public static bool UsesPictures(Settings cfg) =>
        (cfg.LargeImage + cfg.SmallImage).Contains("_image}", StringComparison.OrdinalIgnoreCase);

    /// Fills placeholders, drops empty segments ("GT3 ·  · Race" → "GT3 · Race") and enforces
    /// Discord's 2–128 character limit. Returns null when nothing is left to show.
    public static string? Fill(string template, Dictionary<string, string> values)
    {
        var text = PlaceholderPattern().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) ? v.Trim() : m.Value);

        foreach (var sep in Separators)
            text = string.Join(sep, $" {text} ".Split(sep).Select(p => p.Trim()).Where(p => p.Length > 0));

        text = MultiSpacePattern().Replace(text, " ").Trim();
        if (text.Length < 2)
            return null;
        return Truncate(text, MaxText);
    }

    static string FormatTimeLeft(double seconds) =>
        seconds >= 3600 ? $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m left"
        : seconds >= 60 ? $"{(int)(seconds / 60)} min left"
        : "<1 min left";

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpacePattern();
}
