// Pretends to be the iRacing sim by writing the SDK shared-memory layout, so iRacingPresence can be
// tested without launching iRacing. It uses its own name rather than the real
// Local\IRSDKMemMapFileName, because other iRacing tools (Garage61, Trading Paints, ...) keep the
// real one open and would otherwise pick up the fake session.
//
//   dotnet run --project tools/FakeSim -- [weekend|lap-race|crash] [seconds]
//   iRacingPresence.exe --dry-run --memmap Local\IRSDKMemMapFileName.FakeSim
//
//   weekend   official GT3 event: practice (garage, then on track) → qualifying → timed race
//   lap-race  league race over a fixed number of laps
//   crash     like weekend, but stops updating after 8 s without clearing the status (sim crash)

using System.IO.MemoryMappedFiles;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var cp1252 = Encoding.GetEncoding(1252);

var scenario = args.Length > 0 ? args[0] : "weekend";
var seconds = args.Length > 1 ? double.Parse(args[1]) : 60;

// name, irsdk type (0 char, 1 bool, 2 int, 3 bitfield, 4 float, 5 double)
(string Name, int Type)[] vars =
[
    ("SessionTime", 5), ("SessionNum", 2), ("SessionState", 2), ("SessionTimeRemain", 5),
    ("SessionLapsTotal", 2), ("SessionLapsRemainEx", 2), ("Lap", 2),
    ("PlayerCarPosition", 2), ("PlayerCarClassPosition", 2), ("PlayerCarMyIncidentCount", 2),
    ("LapBestLapTime", 4), ("IsOnTrack", 1), ("IsInGarage", 1), ("IsReplayPlaying", 1), ("OnPitRoad", 1),
];

const int HeaderSize = 112, VarHeaderSize = 144, NumBuf = 3, YamlOffset = 8192, YamlCapacity = 65536;
var offsets = new Dictionary<string, (int Offset, int Type)>();
int bufLen = 0;
foreach (var (name, type) in vars)
{
    offsets[name] = (bufLen, type);
    bufLen += type switch { 0 or 1 => 1, 5 => 8, _ => 4 };
}
bufLen = (bufLen + 15) / 16 * 16;
int bufStart = YamlOffset + YamlCapacity;

using var mmf = MemoryMappedFile.CreateNew(@"Local\IRSDKMemMapFileName.FakeSim", bufStart + NumBuf * bufLen);
using var view = mmf.CreateViewAccessor();

view.Write(0, 2);                 // ver
view.Write(8, 60);                // tickRate
view.Write(16, YamlCapacity);     // sessionInfoLen
view.Write(20, YamlOffset);       // sessionInfoOffset
view.Write(24, vars.Length);      // numVars
view.Write(28, HeaderSize);       // varHeaderOffset
view.Write(32, NumBuf);           // numBuf
view.Write(36, bufLen);           // bufLen
for (int b = 0; b < NumBuf; b++)
    view.Write(48 + b * 16 + 4, bufStart + b * bufLen);

for (int i = 0; i < vars.Length; i++)
{
    long at = HeaderSize + i * VarHeaderSize;
    view.Write(at, vars[i].Type);
    view.Write(at + 4, offsets[vars[i].Name].Offset);
    view.Write(at + 8, 1);
    var nameBytes = new byte[32];
    Encoding.ASCII.GetBytes(vars[i].Name).CopyTo(nameBytes, 0);
    view.WriteArray(at + 16, nameBytes, 0, 32);
}

int sessionInfoUpdate = 0;
void WriteYaml(string yaml)
{
    var bytes = new byte[YamlCapacity];
    cp1252.GetBytes(yaml).CopyTo(bytes, 0);
    view.WriteArray(YamlOffset, bytes, 0, bytes.Length);
    view.Write(12, ++sessionInfoUpdate);
}

view.Write(4, 1); // status: connected
Console.WriteLine($"FakeSim running scenario '{scenario}' for {seconds}s. Ctrl+C to stop.");

var started = DateTime.UtcNow;
int tick = 0, lastSession = -1;
var buffer = new byte[bufLen];

void Set(string name, double value)
{
    var (offset, type) = offsets[name];
    switch (type)
    {
        case 0 or 1: buffer[offset] = (byte)(value != 0 ? 1 : 0); break;
        case 4: BitConverter.TryWriteBytes(buffer.AsSpan(offset), (float)value); break;
        case 5: BitConverter.TryWriteBytes(buffer.AsSpan(offset), value); break;
        default: BitConverter.TryWriteBytes(buffer.AsSpan(offset), (int)value); break;
    }
}

while (true)
{
    double t = (DateTime.UtcNow - started).TotalSeconds;
    if (t > seconds)
        break;
    if (scenario == "crash" && t > 8)
    {
        Thread.Sleep(100);
        continue;
    }

    bool lapRace = scenario == "lap-race";
    int session = lapRace ? 2 : t < 15 ? 0 : t < 30 ? 1 : 2;
    if (session != lastSession)
    {
        WriteYaml(Yaml(lapRace, session));
        lastSession = session;
        Console.WriteLine($"  t={t:0}s session {session}");
    }

    double intoSession = lapRace ? 600 + t : session == 0 ? t : session == 1 ? t - 15 : t - 30;
    double sessionLength = session == 0 ? 1800 : session == 1 ? 600 : 1200;
    bool inGarage = session == 0 && t < 6;

    Array.Clear(buffer);
    Set("SessionTime", intoSession);
    Set("SessionNum", session);
    Set("SessionState", 4); // racing
    Set("SessionTimeRemain", lapRace ? 604800 : sessionLength - intoSession);
    Set("SessionLapsTotal", lapRace ? 20 : 32767);
    Set("SessionLapsRemainEx", lapRace ? 13 : 32767);
    Set("Lap", lapRace ? 7 : 1 + (int)(intoSession / 140));
    Set("PlayerCarPosition", session == 0 ? 0 : 3);
    Set("PlayerCarClassPosition", session == 0 ? 0 : 2);
    Set("PlayerCarMyIncidentCount", 2);
    Set("LapBestLapTime", session == 0 ? 0 : 137.512);
    Set("IsOnTrack", inGarage ? 0 : 1);
    Set("IsInGarage", inGarage ? 1 : 0);
    Set("IsReplayPlaying", 0);
    Set("OnPitRoad", 0);

    int slot = tick % NumBuf;
    view.WriteArray(bufStart + slot * bufLen, buffer, 0, bufLen);
    view.Write(48 + slot * 16, ++tick);
    Thread.Sleep(100);
}

view.Write(4, 0); // status: disconnected, like the sim does on exit
Console.WriteLine("FakeSim stopped.");

static string Yaml(bool lapRace, int current)
{
    string weekend = lapRace
        ? """
           TrackName: roadamerica full
           TrackID: 18
           TrackDisplayName: Road America
           TrackDisplayShortName: Road America
           TrackConfigName: Full Course
           TrackCity: Elkhart Lake
           TrackCountry: USA
           SeriesID: 0
           SeasonID: 0
           SessionID: 0
           SubSessionID: 99887766
           LeagueID: 4242
           Official: 0
           RaceWeek: 0
           EventType: Race
           Category: Road
           NumCarClasses: 2
          """
        : """
           TrackName: spa 2024 up
           TrackID: 524
           TrackLength: 6.93 km
           TrackDisplayName: Circuit de Spa-Francorchamps
           TrackDisplayShortName: Spa
           TrackConfigName: Grand Prix Pits
           TrackCity: Stavelot
           TrackCountry: Belgium
           SeriesID: 4012
           SeasonID: 5123
           SessionID: 271828
           SubSessionID: 31415926
           LeagueID: 0
           Official: 1
           RaceWeek: 3
           EventType: Race
           Category: Road
           NumCarClasses: 1
          """;

    string sessions = lapRace
        ? """
           - SessionNum: 2
             SessionLaps: 20
             SessionTime: 10800.0000 sec
             SessionType: Race
             SessionName: RACE
             ResultsPositions:
             - Position: 1
               CarIdx: 3
             - Position: 2
               CarIdx: 5
             ResultsFastestLap:
             - CarIdx: 3
               FastestLap: 4
          """
        : """
           - SessionNum: 0
             SessionLaps: unlimited
             SessionTime: 1800.0000 sec
             SessionNumLapsToAvg: 0
             SessionType: Practice
             SessionTrackRubberState: moderate usage
             SessionName: PRACTICE
             SessionSubType:
             ResultsPositions:
             ResultsFastestLap:
             - CarIdx: 255
               FastestLap: 0
               FastestTime: -1.0000
             ResultsAverageLapTime: -1.0000
           - SessionNum: 1
             SessionLaps: unlimited
             SessionTime: 600.0000 sec
             SessionType: Open Qualify
             SessionName: QUALIFY
             ResultsPositions:
           - SessionNum: 2
             SessionLaps: unlimited
             SessionTime: 1200.0000 sec
             SessionType: Race
             SessionName: RACE
             ResultsPositions:
             - Position: 1
               ClassPosition: 0
               CarIdx: 3
               Lap: 0
             - Position: 2
               CarIdx: 5
          """;

    // Driver and team names are deliberately awkward: iRacing doesn't quote or escape them.
    string drivers = $"""
         DriverInfo:
          DriverCarIdx: 5
          DriverUserID: 111111
          PaceCarIdx: 0
          Drivers:
          - CarIdx: 0
            UserName: Pace Car
            TeamName: Pace Car
            CarNumber: "0"
            CarClassID: 11
            CarIsPaceCar: 1
            CarIsAI: 0
            CarScreenName: safety pcporsche911cup
            IsSpectator: 0
          - CarIdx: 3
            UserName: Jean-Éric O'Brien
            TeamName: Team: #1 "Fast" Racing
            CarNumber: "17"
            CarClassID: 4083
            CarIsPaceCar: 0
            CarIsAI: 0
            CarScreenName: {(lapRace ? "Mercedes-AMG GT4" : "Ferrari 296 GT3")}
            CarClassShortName: {(lapRace ? "GT4" : "GT3")}
            IRating: 3120
            LicString: A 3.41
            IsSpectator: 0
          - CarIdx: 5
            UserName: Test Driver
            TeamName: Test Driver
            CarNumber: "42"
            CarClassID: 4083
            CarIsPaceCar: 0
            CarIsAI: 0
            CarScreenName: {(lapRace ? "BMW M4 G82 GT4" : "Porsche 911 GT3 R (992)")}
            CarScreenNameShort: {(lapRace ? "BMW M4 GT4" : "911 GT3 R")}
            CarClassShortName: {(lapRace ? "GT4" : "GT3")}
            IRating: 2450
            LicString: B 2.87
            IsSpectator: 0
          - CarIdx: 7
            UserName: Some Spectator
            CarClassID: 4083
            CarIsPaceCar: 0
            IsSpectator: 1
          - CarIdx: 8
            UserName: Other Class
            CarClassID: {(lapRace ? "2708" : "4083")}
            CarScreenName: Cadillac V-Series.R
            CarIsPaceCar: 0
            IsSpectator: 0
         """;

    return $"""
        ---
        WeekendInfo:
        {weekend}
         WeekendOptions:
          NumStarters: 24
          StartingGrid: 2x2 inline pole on left
         TelemetryOptions:
          TelemetryDiskFile: ""

        SessionInfo:
         CurrentSessionNum: {current}
         Sessions:
        {sessions}

        {drivers}
        ...

        """;
}
