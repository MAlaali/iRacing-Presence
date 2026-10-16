using IRacingPresence.IRacing;

namespace IRacingPresence;

/// Fake sessions for --demo, so the Discord side can be checked without launching iRacing.
static class DemoStates
{
    static readonly TimeSpan StepLength = TimeSpan.FromSeconds(20);

    static readonly SimState[] Steps =
    [
        new()
        {
            KnownSeriesName = "GT Sprint", Session = "Practice", SessionKey = "practice", SessionNum = 0,
            Track = "Spa", TrackFull = "Circuit de Spa-Francorchamps", TrackConfig = "Grand Prix Pits",
            Car = "Porsche 911 GT3 R (992)", CarClass = "GT3", Status = DriverStatus.InGarage,
            TimeRemaining = 1500, FieldSize = 24, Position = 0, SubSessionId = 1,
        },
        new()
        {
            KnownSeriesName = "GT Sprint", Session = "Qualifying", SessionKey = "qualify", SessionNum = 1,
            Track = "Spa", TrackFull = "Circuit de Spa-Francorchamps", TrackConfig = "Grand Prix Pits",
            Car = "Porsche 911 GT3 R (992)", CarClass = "GT3", Status = DriverStatus.Driving,
            TimeRemaining = 540, FieldSize = 24, Position = 5, SubSessionId = 1,
        },
        new()
        {
            KnownSeriesName = "GT Sprint", Session = "Race", SessionKey = "race", SessionNum = 2,
            Track = "Spa", TrackFull = "Circuit de Spa-Francorchamps", TrackConfig = "Grand Prix Pits",
            Car = "Porsche 911 GT3 R (992)", CarClass = "GT3", Status = DriverStatus.Driving,
            TimeRemaining = 1200, FieldSize = 24, Position = 3, SubSessionId = 1,
        },
        new()
        {
            KnownSeriesName = "IMSA Michelin Pilot Challenge", Session = "Race", SessionKey = "race", SessionNum = 2,
            Track = "Road America", TrackFull = "Road America", TrackConfig = "Full Course",
            Car = "BMW M4 G82 GT4", CarClass = "GT4", Status = DriverStatus.Driving,
            LapsTotal = 18, Lap = 7, SessionElapsed = 1100, FieldSize = 30, Position = 8, SubSessionId = 2,
        },
    ];

    public static SimState At(TimeSpan sinceStart)
    {
        int step = (int)(sinceStart / StepLength);
        var state = Steps[step % Steps.Length];
        // Make the clocks tick while each step is shown.
        var intoStep = sinceStart - step * StepLength;
        return state with
        {
            TimeRemaining = state.TimeRemaining - intoStep.TotalSeconds,
            SessionElapsed = state.SessionElapsed + intoStep.TotalSeconds,
        };
    }
}
