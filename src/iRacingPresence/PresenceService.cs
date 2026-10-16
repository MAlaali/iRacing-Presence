using System.Diagnostics;
using IRacingPresence.Discord;
using IRacingPresence.IRacing;

namespace IRacingPresence;

sealed record ServiceStatus(
    bool IRacingOpen,
    bool InSession,
    bool DiscordConnected,
    bool Paused,
    string? Problem,
    string? Details,
    string? State,
    string? NameKey,
    string? SeriesName,
    SimState? Sim);

/// Command-line options; everything except the defaults is for testing.
sealed record RunOptions(bool Demo, bool DryRun, string MemMapName, string? DiscordPipe, bool Welcome = false);

/// Polls iRacing, builds the presence and keeps Discord in sync with it.
sealed class PresenceService(SettingsStore settings, NameBook names, ImageCatalog images, RunOptions options)
{
    // Discord accepts 5 activity updates per 20 seconds.
    static readonly TimeSpan MinSendGap = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(15);
    // A pipe only reports that Discord went away when we next write to it, so re-send the
    // current activity this often even when nothing changed. That also restores the status
    // after Discord restarts.
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    readonly bool demo = options.Demo, dryRun = options.DryRun;

    readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    volatile bool _paused;

    public event Action<ServiceStatus>? StatusChanged;
    /// Raised the first time a series or league without a saved name shows up.
    public event Action<string, SimState>? UnnamedSeriesSeen;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reader = new IRacingReader(options.MemMapName);
        using var discord = new DiscordIpc(options.DiscordPipe);
        var demoStart = DateTimeOffset.Now;
        DateTimeOffset? idleSince = null;
        DiscordActivity? sent = null;
        bool haveSent = false;
        var lastSend = DateTime.MinValue;
        var nextConnect = DateTime.MinValue;
        string? discordProblem = null;
        string? connectedWithId = null;
        bool fellBackToPlaying = false;
        string? lastLogged = null;

        Log.Info($"Started{(demo ? " in demo mode" : "")}{(dryRun ? " (dry run: nothing is sent to Discord)" : "")}.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // One bad update must never stop the app: log it and carry on with the next one.
                Log.Error($"Unexpected error, carrying on: {e}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.Current.UpdateIntervalSeconds, 1, 30)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (discord.IsConnected)
        {
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await discord.SetActivityAsync(null, bye.Token);
            }
            catch (Exception e) when (e is DiscordException or OperationCanceledException) { }
        }
        Log.Info("Stopped.");

        async Task TickAsync()
        {
            var cfg = settings.Current;
            var now = DateTimeOffset.Now;

            SimState? sim = null;
            try
            {
                sim = demo ? DemoStates.At(now - demoStart) : reader.Read() is { } snap ? SimState.From(snap) : null;
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read iRacing data: {e.Message}");
            }
            bool iRacingOpen = sim != null || IsRunning(cfg.IRacingProcesses);

            if (sim != null && PresenceBuilder.UsesPictures(cfg))
                sim = sim with { CarImage = images.Car(sim.Car), TrackImage = images.Track(sim.TrackFull) };

            string? seriesName = null;
            if (sim != null)
            {
                seriesName = sim.KnownSeriesName ?? (sim.NameKey is { } key ? names.Get(key) : null);
                if (seriesName == null && sim.NameKey is { } unnamed)
                {
                    names.Remember(unnamed);
                    if (_announced.Add(unnamed))
                    {
                        Log.Info($"No name saved for {unnamed} ({sim.Car} @ {sim.TrackFull}). Add one in names.json or from the tray menu.");
                        UnnamedSeriesSeen?.Invoke(unnamed, sim);
                    }
                }
                seriesName ??= sim.FallbackSeriesName;
            }

            DiscordActivity? desired = null;
            if (!Paused)
            {
                if (sim != null)
                {
                    idleSince = null;
                    desired = PresenceBuilder.Build(sim, seriesName!, cfg, now);
                }
                else if (iRacingOpen && cfg.Idle.Enabled)
                {
                    idleSince ??= now;
                    desired = PresenceBuilder.BuildIdle(cfg, idleSince.Value);
                }
            }
            if (desired != null && fellBackToPlaying)
                desired.Type = 0;

            // Keep the Discord connection only while there's something to show.
            string? problem = settings.LoadError != null ? $"settings.json has an error: {settings.LoadError}" : null;
            if (!dryRun)
            {
                if (!cfg.HasApplicationId)
                {
                    problem ??= "Paste your Discord Application ID into settings.json.";
                    if (discord.IsConnected)
                        discord.Disconnect();
                }
                else
                {
                    if (discord.IsConnected && connectedWithId != cfg.DiscordApplicationId)
                    {
                        discord.Disconnect();
                        nextConnect = DateTime.MinValue;
                    }

                    if (desired != null && !discord.IsConnected && DateTime.UtcNow >= nextConnect)
                    {
                        try
                        {
                            await discord.ConnectAsync(cfg.DiscordApplicationId, ct);
                            connectedWithId = cfg.DiscordApplicationId;
                            discordProblem = null;
                            haveSent = false;
                            Log.Info($"Connected to Discord{(discord.UserName != null ? $" as {discord.UserName}" : "")}.");
                        }
                        catch (DiscordException e)
                        {
                            if (discordProblem != e.Message)
                                Log.Warn($"Discord: {e.Message}");
                            discordProblem = e.Message;
                            nextConnect = DateTime.UtcNow + ReconnectDelay;
                        }
                    }
                    else if (desired == null && !iRacingOpen && discord.IsConnected && (!haveSent || sent == null))
                    {
                        // iRacing closed and the presence is already cleared: let go of Discord.
                        discord.Disconnect();
                        Log.Info("iRacing closed; disconnected from Discord.");
                    }
                }
            }

            bool changed = !haveSent || !DiscordActivity.LooksTheSame(desired, sent);
            bool keepAliveDue = !dryRun && discord.IsConnected && sent != null && DateTime.UtcNow - lastSend >= KeepAlive;
            if ((changed || keepAliveDue) && DateTime.UtcNow - lastSend >= MinSendGap)
            {
                if (dryRun)
                {
                    var json = desired?.ToJson() ?? "(cleared)";
                    if (json != lastLogged)
                        Log.Info($"Presence: {json}");
                    lastLogged = json;
                    sent = desired;
                    haveSent = true;
                    lastSend = DateTime.UtcNow;
                }
                else if (discord.IsConnected)
                {
                    try
                    {
                        await discord.SetActivityAsync(desired, ct);
                        sent = desired;
                        haveSent = true;
                        discordProblem = null;
                        if (changed)
                            Log.Info(desired == null ? "Presence cleared." : $"Presence: {desired.Details} | {desired.State}");
                    }
                    catch (DiscordException e) when (desired is { Type: not 0 } && !fellBackToPlaying && discord.IsConnected)
                    {
                        Log.Warn($"Discord refused activity type {desired.Type} ({e.Message}); using \"playing\" instead.");
                        fellBackToPlaying = true;
                    }
                    catch (DiscordException e)
                    {
                        Log.Warn($"Discord: {e.Message}");
                        discordProblem = e.Message;
                        haveSent = false;
                    }
                    lastSend = DateTime.UtcNow;
                }
            }

            StatusChanged?.Invoke(new ServiceStatus(
                IRacingOpen: iRacingOpen,
                InSession: sim != null,
                DiscordConnected: dryRun || discord.IsConnected,
                Paused: Paused,
                Problem: problem ?? (desired != null ? discordProblem : null),
                Details: desired?.Details,
                State: desired?.State,
                NameKey: sim?.NameKey,
                SeriesName: seriesName,
                Sim: sim));
        }
    }

    static bool IsRunning(IEnumerable<string> processNames)
    {
        foreach (var name in processNames)
        {
            var found = Process.GetProcessesByName(name);
            bool running = found.Length > 0;
            foreach (var p in found)
                p.Dispose();
            if (running)
                return true;
        }
        return false;
    }
}
