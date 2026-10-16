using IRacingPresence.IRacing;

namespace IRacingPresence;

/// Fake sessions for --demo and the settings preview, so the Discord side can be checked without
/// launching iRacing.
static class DemoStates
{
    static readonly TimeSpan StepLength = TimeSpan.FromSeconds(20);

    static readonly SimState Spa = new()
    {
        KnownSeriesName = "GT Sprint", Track = "Spa", TrackFull = "Circuit de Spa-Francorchamps", TrackConfig = "Grand Prix Pits",
        Car = "Porsche 911 GT3 R (992)", CarClass = "GT3", DriverName = "Demo Driver", IRating = "2450", License = "B 2.87",
        FieldSize = 24, SubSessionId = 1, AirTempC = 21,
    };

    public static readonly (string Name, SimState State)[] Samples =
    [
        ("Practice, in the garage", Spa with
        {
            Session = "Practice", SessionKey = "practice", SessionNum = 0, Status = DriverStatus.InGarage,
            Moment = RaceMoment.InGarage, TimeRemaining = 1500,
        }),
        ("Qualifying", Spa with
        {
            Session = "Qualifying", SessionKey = "qualify", SessionNum = 1, Status = DriverStatus.Driving,
            TimeRemaining = 540, Position = 5, BestLap = 137.512,
        }),
        ("Race", Spa with
        {
            Session = "Race", SessionKey = "race", SessionNum = 2, Status = DriverStatus.Driving,
            TimeRemaining = 1200, Position = 3, BestLap = 137.104,
        }),
        ("Race, last lap", Spa with
        {
            Session = "Race", SessionKey = "race", SessionNum = 2, Status = DriverStatus.Driving,
            Moment = RaceMoment.LastLap, TimeRemaining = 95, Position = 3, BestLap = 137.104,
        }),
        ("Race, finished", FinishedSample()),
        ("Endurance, rain at night", new SimState
        {
            KnownSeriesName = "IMSA Michelin Pilot Challenge", Session = "Race", SessionKey = "race", SessionNum = 2,
            Track = "Road America", TrackFull = "Road America", TrackConfig = "Full Course",
            Car = "BMW M4 G82 GT4", CarClass = "GT4", Team = "Demo Racing", DriverName = "Demo Driver",
            IRating = "2450", License = "B 2.87", Moment = RaceMoment.Caution, Multiclass = true,
            LapsTotal = 45, Lap = 17, SessionElapsed = 2400, FieldSize = 38, ClassFieldSize = 20, Position = 14,
            ClassPosition = 6, SubSessionId = 2, Raining = true, Night = true, AirTempC = 14, BestLap = 152.877,
            Status = DriverStatus.Driving,
        }),
    ];

    public static SimState FinishedSample() => Spa with
    {
        Session = "Race", SessionKey = "race", SessionNum = 2, Status = DriverStatus.Finished,
        Moment = RaceMoment.Finished, SessionState = SimState.StateCheckered, Position = 3, BestLap = 137.104,
        Result = new RaceResult(3, 3, 17, 137.104, 2, "Running"),
    };

    public static SimState At(TimeSpan sinceStart)
    {
        int step = (int)(sinceStart / StepLength);
        var state = Samples[step % Samples.Length].State;
        // Make the clocks tick while each step is shown.
        var intoStep = sinceStart - step * StepLength;
        return state with
        {
            TimeRemaining = state.TimeRemaining - intoStep.TotalSeconds,
            SessionElapsed = state.SessionElapsed + intoStep.TotalSeconds,
        };
    }
}
