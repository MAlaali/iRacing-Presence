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
        var values = Placeholders(s, seriesName, cfg.UsesFahrenheit);
        // Watching someone else gets its own second line ("Watching Max Verstappen @ Spa").
        var stateTemplate = s.WatchingName.Length > 0 ? cfg.SpectateState : cfg.State;
        var activity = NewActivity(cfg, Fill(cfg.Details, values), Fill(stateTemplate, values));
        activity.Assets = Assets(cfg, values);

        if (s.Moment == RaceMoment.Finished)
            activity.Timestamps = null; // the race is over; a clock would only confuse
        else if (cfg.ShowTimeLeft && s.LapsTotal == null && s.TimeRemaining is double left)
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
        // No session, so every placeholder is empty (rather than left showing as "{car}").
        var values = Placeholders(new SimState(), "").ToDictionary(p => p.Key, _ => "");
        var activity = NewActivity(cfg, Fill(cfg.Idle.Details, values), Fill(cfg.Idle.State, values));
        activity.Type = 0;
        activity.Assets = Assets(cfg, values);
        if (cfg.ShowElapsedTime)
            activity.Timestamps = new ActivityTimestamps { Start = idleSince.ToUnixTimeMilliseconds() };
        return activity;
    }

    public static Dictionary<string, string> Placeholders(SimState s, string seriesName, bool fahrenheit = false)
    {
        var inv = CultureInfo.InvariantCulture;
        string laps = s.LapsTotal is int total ? $"Lap {Math.Min(Math.Max(s.Lap, 1), total)}/{total}"
            : s.Lap > 0 ? $"Lap {s.Lap}" : "";
        string bestLap = s.BestLap > 0 ? TimeSpan.FromSeconds(s.BestLap).ToString(@"m\:ss\.fff", inv) : "";
        string irating = s.IRating is "" or "0" ? "" : s.IRating;
        return new Dictionary<string, string>
        {
            ["moment"] = MomentText(s.Moment),
            ["rating"] = irating.Length > 0 ? $"{irating} iR" : "",
            ["best"] = bestLap.Length > 0 ? $"Best {bestLap}" : "",
            ["conditions"] = Conditions(s, fahrenheit),
            ["team"] = s.Team,
            ["driver"] = s.DriverName,
            ["watching"] = s.WatchingName.Length > 0 ? $"Watching {s.WatchingName}" : "",
            ["watching_car"] = s.WatchingCar,
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
            // Everyone in the session (all classes). Left out when you're alone, e.g. a test drive.
            ["players"] = s.FieldSize >= 2 ? $"{s.FieldSize.ToString(inv)} drivers" : "",
            ["standing"] = Standing(s),
            ["class_field_size"] = s.ClassFieldSize > 0 ? s.ClassFieldSize.ToString(inv) : "",
            ["lap"] = s.Lap > 0 ? s.Lap.ToString(inv) : "",
            ["laps_total"] = s.LapsTotal?.ToString(inv) ?? "",
            ["laps"] = laps,
            // Lap races have no useful countdown, so show the lap count instead. "Last lap" and
            // "Finished" already say where the race is.
            ["progress"] = s.LapsTotal != null && s.IsRace && s.Moment is not (RaceMoment.LastLap or RaceMoment.Finished) ? laps : "",
            ["time_left"] = s.TimeRemaining is double left ? FormatTimeLeft(left) : "",
            ["incidents"] = $"{s.Incidents}x",
            ["best_lap"] = bestLap,
            ["irating"] = irating,
            ["license"] = s.License,
        };
    }

    public static string MomentText(RaceMoment moment) => moment switch
    {
        RaceMoment.InGarage => "In the garage",
        RaceMoment.InPits => "In the pits",
        RaceMoment.Grid => "On the grid",
        RaceMoment.Formation => "Formation lap",
        RaceMoment.Caution => "Caution",
        RaceMoment.RedFlag => "Red flag",
        RaceMoment.LastLap => "Last lap",
        RaceMoment.Finished => "🏁 Finished",
        RaceMoment.Spectating => "Spectating",
        _ => "",
    };

    /// "Rain · Night · 14°C": only what's unusual, plus the air temperature.
    static string Conditions(SimState s, bool fahrenheit)
    {
        var parts = new List<string>();
        if (s.Raining) parts.Add("Rain");
        else if (s.Wet) parts.Add("Wet track");
        else if (s.Damp) parts.Add("Damp track");
        if (s.Night) parts.Add("Night");
        if (s.AirTempC is double c)
            parts.Add(fahrenheit ? $"{Math.Round(c * 9 / 5 + 32):0}°F" : $"{Math.Round(c):0}°C");
        return string.Join(" · ", parts);
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

    /// "P3 of 24" (your class position in multiclass: "P2 of 12 in GT3"), or "24 drivers" until
    /// you have a position (before a lap time in practice), or nothing when you're alone.
    public static string Standing(SimState s)
    {
        var inv = CultureInfo.InvariantCulture;
        // Once the flag is out, the official results are the final word.
        int position = s.Moment == RaceMoment.Finished && s.Result is { Position: > 0 } r ? r.Position : s.Position;
        int classPosition = s.Moment == RaceMoment.Finished && s.Result is { ClassPosition: > 0 } rc ? rc.ClassPosition : s.ClassPosition;
        if (s.Multiclass && classPosition > 0 && s.ClassFieldSize >= classPosition)
        {
            var cls = s.CarClass.Length > 0 ? s.CarClass : "class";
            return $"P{classPosition.ToString(inv)} of {s.ClassFieldSize.ToString(inv)} in {cls}";
        }
        if (position > 0 && s.FieldSize >= position)
            return $"P{position.ToString(inv)} of {s.FieldSize.ToString(inv)}";
        return s.FieldSize >= 2 ? $"{s.FieldSize.ToString(inv)} drivers" : "";
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
