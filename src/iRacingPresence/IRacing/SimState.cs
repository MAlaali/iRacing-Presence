using System.Globalization;

namespace IRacingPresence.IRacing;

enum DriverStatus { Driving, InPits, InGarage, Spectating, Finished }

/// The thing worth mentioning right now, if any. Race-only moments come from the session state
/// and the flags the sim is showing you.
enum RaceMoment { None, InGarage, InPits, Grid, Formation, Caution, RedFlag, LastLap, Finished, Spectating }

/// Your line in the session's official results (SessionInfo › Sessions › ResultsPositions).
sealed record RaceResult(int Position, int ClassPosition, int LapsComplete, double FastestTime, int Incidents, string ReasonOut);

/// Everything the presence needs about the current session, pulled out of one snapshot.
sealed record SimState
{
    // irsdk_SessionState: 0 invalid, 1 get in car, 2 warmup, 3 parade laps, 4 racing, 5 checkered, 6 cool down
    public const int StateGetInCar = 1, StateWarmup = 2, StateParadeLaps = 3, StateRacing = 4, StateCheckered = 5, StateCoolDown = 6;
    // irsdk_Flags bits used here
    const int FlagWhite = 0x0002, FlagRed = 0x0010, FlagCaution = 0x4000, FlagCautionWaving = 0x8000;
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
    public bool TeamRacing { get; init; }

    public int SessionNum { get; init; }
    public string Session { get; init; } = "";
    public string SessionKey { get; init; } = "";
    public int SessionState { get; init; } = StateRacing;
    public RaceMoment Moment { get; init; }
    public double? TimeRemaining { get; init; }
    public double SessionElapsed { get; init; }
    public int? LapsTotal { get; init; }
    public int Lap { get; init; }

    public string DriverName { get; init; } = "";
    public string Team { get; init; } = "";
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
    public RaceResult? Result { get; init; }

    /// The driver and car the camera is on while you're spectating someone else.
    public string WatchingName { get; init; } = "";
    public string WatchingCar { get; init; } = "";

    // Weather and light
    public double? AirTempC { get; init; }
    public bool Wet { get; init; }
    public bool Damp { get; init; }
    public bool Raining { get; init; }
    public bool Night { get; init; }

    /// Set by demo mode, which has no names.json entry to look up.
    public string? KnownSeriesName { get; init; }

    /// Picture links filled in from ImageCatalog.
    public string? CarImage { get; init; }
    public string? TrackImage { get; init; }

    public bool IsRace => SessionKey == "race";

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
        var status = player == null ? DriverStatus.Spectating
            : !t.Bool("IsOnTrack") ? t.Bool("IsInGarage") ? DriverStatus.InGarage : DriverStatus.Spectating
            : sessionState >= StateCheckered ? DriverStatus.Finished
            : t.Bool("OnPitRoad") ? DriverStatus.InPits
            : DriverStatus.Driving;

        var (sessionLabel, sessionKey) = DescribeSession(session.Str("SessionType"), session.Str("SessionName"));
        int flags = t.Int("SessionFlags");

        // Who the camera is on, when it isn't you.
        int camIdx = t.Int("CamCarIdx", -1);
        var watched = status == DriverStatus.Spectating && camIdx != playerIdx
            ? drivers.FirstOrDefault(d => d.Int("CarIdx", -1) == camIdx && d.Str("CarIsPaceCar") != "1")
            : null;

        var resultLine = session.Items("ResultsPositions").FirstOrDefault(r => r.Int("CarIdx", -1) == playerIdx);
        var result = resultLine == null || player == null ? null : new RaceResult(
            resultLine.Int("Position"),
            resultLine.Int("ClassPosition", -1) + 1, // 0-based in the results
            resultLine.Int("LapsComplete"),
            resultLine.Double("FastestTime"),
            resultLine.Int("Incidents"),
            resultLine.Str("ReasonOutStr"));

        // A pure spectator has no car, position or laps of their own; describe the car being watched.
        bool spectator = player == null;
        var carOwner = spectator ? watched : player;

        // TrackWetness: 1 dry, 2 mostly dry, 3 very lightly wet, 4 lightly wet, 5 moderately wet, 6+ very wet
        int wetness = t.Int("TrackWetness");
        double? airTemp = t.Number("AirTemp") is double a && a > -60 && a < 70 ? a : ParseLeadingNumber(weekend.Str("TrackAirTemp"));
        bool teamRacing = weekend.Str("TeamRacing") == "1";

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
            TeamRacing = teamRacing,

            SessionNum = sessionNum,
            Session = sessionLabel,
            SessionKey = sessionKey,
            SessionState = sessionState,
            Moment = MomentFor(sessionKey == "race", sessionState, flags, status),
            TimeRemaining = timeLimited && sessionState < StateCheckered ? remain : null,
            SessionElapsed = Math.Max(0, t.Double("SessionTime", 0)),
            LapsTotal = lapLimited ? lapsTotal : null,
            Lap = Math.Max(0, t.Int("Lap")),

            DriverName = player.Str("UserName"),
            Team = teamRacing ? player.Str("TeamName") : "",
            Car = carOwner.Str("CarScreenName"),
            CarShort = FirstNonEmpty(carOwner.Str("CarScreenNameShort"), carOwner.Str("CarScreenName")),
            CarClass = carOwner.Str("CarClassShortName"),
            CarNumber = carOwner.Str("CarNumber"),
            IRating = player.Str("IRating"),
            License = player.Str("LicString"),

            Multiclass = weekend.Int("NumCarClasses", 1) > 1,
            Position = spectator ? 0 : t.Int("PlayerCarPosition"),
            ClassPosition = spectator ? 0 : t.Int("PlayerCarClassPosition"),
            FieldSize = racers.Count,
            ClassFieldSize = racers.Count(d => d.Str("CarClassID") == playerClass),
            Incidents = spectator ? 0 : t.Int("PlayerCarMyIncidentCount"),
            BestLap = spectator ? 0 : t.Double("LapBestLapTime", 0),
            Status = status,
            Result = result,

            WatchingName = watched.Str("UserName"),
            WatchingCar = watched.Str("CarScreenName"),

            AirTempC = airTemp,
            Wet = wetness >= 5 || t.Bool("WeatherDeclaredWet"),
            Damp = wetness is 3 or 4,
            Raining = t.Double("Precipitation", 0) > 0.05,
            Night = t.Number("SolarAltitude") is double sun && sun < -0.05,
        };
    }

    static RaceMoment MomentFor(bool race, int state, int flags, DriverStatus status)
    {
        if (status == DriverStatus.Spectating)
            return RaceMoment.Spectating;
        if (race)
        {
            if (state >= StateCheckered) return RaceMoment.Finished;
            if ((flags & FlagRed) != 0) return RaceMoment.RedFlag;
            if ((flags & (FlagCaution | FlagCautionWaving)) != 0) return RaceMoment.Caution;
        }
        if (status == DriverStatus.InGarage) return RaceMoment.InGarage;
        if (race && state == StateParadeLaps) return RaceMoment.Formation;
        if (race && state is StateGetInCar or StateWarmup) return RaceMoment.Grid;
        if (status == DriverStatus.InPits) return RaceMoment.InPits;
        if (race && state == StateRacing && (flags & FlagWhite) != 0) return RaceMoment.LastLap;
        return RaceMoment.None;
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

    /// "25.56 C" → 25.56
    static double? ParseLeadingNumber(string text)
    {
        var first = text.Split(' ', 2)[0];
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";
}
