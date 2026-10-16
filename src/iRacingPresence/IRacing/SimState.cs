using System.Globalization;

namespace IRacingPresence.IRacing;

enum DriverStatus { Driving, InPits, InGarage, Spectating, Finished }

/// Everything the presence needs about the current session, pulled out of one snapshot.
sealed record SimState
{
    // irsdk_SessionState: 0 invalid, 1 get in car, 2 warmup, 3 parade laps, 4 racing, 5 checkered, 6 cool down
    const int SessionStateCheckered = 5;
    const int UnlimitedLaps = 32767;
    const double UnlimitedTime = 604000; // the SDK uses 604800 s (one week) for "no limit"

    public string Track { get; init; } = "";
    public string TrackFull { get; init; } = "";
    public string TrackConfig { get; init; } = "";
    public string TrackCountry { get; init; } = "";

    public int SeriesId { get; init; }
    public int LeagueId { get; init; }
    public int SubSessionId { get; init; }
    public bool Official { get; init; }
    public string EventType { get; init; } = "";
    public bool IsAiSession { get; init; }

    public int SessionNum { get; init; }
    public string Session { get; init; } = "";
    public string SessionKey { get; init; } = "";
    public double? TimeRemaining { get; init; }
    public double SessionElapsed { get; init; }
    public int? LapsTotal { get; init; }
    public int Lap { get; init; }

    public string Car { get; init; } = "";
    public string CarShort { get; init; } = "";
    public string CarClass { get; init; } = "";
    public string CarNumber { get; init; } = "";
    public string IRating { get; init; } = "";
    public string License { get; init; } = "";

    public bool Multiclass { get; init; }
    public int Position { get; init; }
    public int ClassPosition { get; init; }
    public int FieldSize { get; init; }
    public int ClassFieldSize { get; init; }
    public int Incidents { get; init; }
    public double BestLap { get; init; }
    public DriverStatus Status { get; init; }

    /// Set by demo mode, which has no names.json entry to look up.
    public string? KnownSeriesName { get; init; }

    /// Picture links filled in from ImageCatalog.
    public string? CarImage { get; init; }
    public string? TrackImage { get; init; }

    /// Key into names.json for sessions whose name iRacing doesn't expose (only IDs are in the SDK).
    public string? NameKey =>
        LeagueId > 0 ? $"league:{LeagueId}" : SeriesId > 0 ? $"series:{SeriesId}" : null;

    public string FallbackSeriesName =>
        EventType.Equals("Test", StringComparison.OrdinalIgnoreCase) ? "Test Drive"
        : EventType.Equals("Time Trial", StringComparison.OrdinalIgnoreCase) ? "Time Trial"
        : IsAiSession ? "AI Session"
        : LeagueId > 0 ? "League Session"
        : SeriesId > 0 ? "iRacing Official"
        : "Hosted Session";

    public static SimState From(TelemetrySnapshot t)
    {
        var info = t.SessionInfo;
        var weekend = info.Map("WeekendInfo");
        var driverInfo = info.Map("DriverInfo");
        var drivers = driverInfo.Items("Drivers").ToList();

        int sessionNum = t.Int("SessionNum");
        var session = info.Map("SessionInfo").Items("Sessions")
            .FirstOrDefault(s => s.Int("SessionNum", -1) == sessionNum);

        int playerIdx = driverInfo.Int("DriverCarIdx", -1);
        var player = drivers.FirstOrDefault(d => d.Int("CarIdx", -1) == playerIdx && d.Str("IsSpectator") != "1");
        var racers = drivers.Where(d => d.Str("CarIsPaceCar") != "1" && d.Str("IsSpectator") != "1").ToList();
        var playerClass = player.Str("CarClassID");

        int lapsTotal = t.Int("SessionLapsTotal", UnlimitedLaps);
        if (lapsTotal <= 0 || lapsTotal >= UnlimitedLaps)
            lapsTotal = session.Int("SessionLaps", UnlimitedLaps);
        bool lapLimited = lapsTotal > 0 && lapsTotal < UnlimitedLaps;

        double remain = t.Double("SessionTimeRemain");
        bool timeLimited = !session.Str("SessionTime").StartsWith("unlimited", StringComparison.OrdinalIgnoreCase)
            && remain > 0 && remain < UnlimitedTime;

        int sessionState = t.Int("SessionState");
        var status = !t.Bool("IsOnTrack")
            ? t.Bool("IsInGarage") ? DriverStatus.InGarage : DriverStatus.Spectating
            : sessionState >= SessionStateCheckered ? DriverStatus.Finished
            : t.Bool("OnPitRoad") ? DriverStatus.InPits
            : DriverStatus.Driving;

        var (sessionLabel, sessionKey) = DescribeSession(session.Str("SessionType"), session.Str("SessionName"));

        return new SimState
        {
            Track = FirstNonEmpty(weekend.Str("TrackDisplayShortName"), weekend.Str("TrackDisplayName"), weekend.Str("TrackName")),
            TrackFull = FirstNonEmpty(weekend.Str("TrackDisplayName"), weekend.Str("TrackName")),
            TrackConfig = weekend.Str("TrackConfigName"),
            TrackCountry = weekend.Str("TrackCountry"),
            SeriesId = weekend.Int("SeriesID"),
            LeagueId = weekend.Int("LeagueID"),
            SubSessionId = weekend.Int("SubSessionID"),
            Official = weekend.Str("Official") == "1",
            EventType = weekend.Str("EventType"),
            IsAiSession = drivers.Any(d => d.Str("CarIsAI") == "1"),

            SessionNum = sessionNum,
            Session = sessionLabel,
            SessionKey = sessionKey,
            TimeRemaining = timeLimited ? remain : null,
            SessionElapsed = Math.Max(0, t.Double("SessionTime", 0)),
            LapsTotal = lapLimited ? lapsTotal : null,
            Lap = Math.Max(0, t.Int("Lap")),

            Car = player.Str("CarScreenName"),
            CarShort = FirstNonEmpty(player.Str("CarScreenNameShort"), player.Str("CarScreenName")),
            CarClass = player.Str("CarClassShortName"),
            CarNumber = player.Str("CarNumber"),
            IRating = player.Str("IRating"),
            License = player.Str("LicString"),

            Multiclass = weekend.Int("NumCarClasses", 1) > 1,
            Position = t.Int("PlayerCarPosition"),
            ClassPosition = t.Int("PlayerCarClassPosition"),
            FieldSize = racers.Count,
            ClassFieldSize = racers.Count(d => d.Str("CarClassID") == playerClass),
            Incidents = t.Int("PlayerCarMyIncidentCount"),
            BestLap = t.Double("LapBestLapTime", 0),
            Status = player == null ? DriverStatus.Spectating : status,
        };
    }

    static (string Label, string Key) DescribeSession(string type, string name)
    {
        var t = type.ToLowerInvariant();
        if (t.Contains("qualify")) return ("Qualifying", "qualify");
        if (t.Contains("practice")) return ("Practice", "practice");
        if (t.Contains("warmup")) return ("Warmup", "warmup");
        if (t.Contains("testing")) return ("Test Drive", "testing");
        if (t.Contains("time trial")) return ("Time Trial", "timetrial");
        if (t.Contains("race"))
        {
            // Heat events name their races HEAT 1, CONSOLATION, FEATURE, ...
            var label = name.Length == 0 || name.Equals("RACE", StringComparison.OrdinalIgnoreCase)
                ? "Race"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
            return (label, "race");
        }
        return (type.Length > 0 ? type : "Session", "session");
    }

    static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";
}
