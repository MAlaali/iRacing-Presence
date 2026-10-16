using System.Globalization;
using System.Text.Json;
using IRacingPresence.IRacing;

namespace IRacingPresence;

/// Posts "🏁 finished P3 of 24" to a Discord channel through a webhook after each race.
/// It waits for the results to settle: until the session cools down, 90 seconds after the flag,
/// or until you leave the race, whichever comes first.
sealed class ResultsReporter
{
    static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(90);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly HashSet<string> _posted = [];
    SimState? _pending;
    string _pendingSeries = "";
    string? _pendingKey;
    DateTime _flagSeenUtc;

    public void Observe(SimState? sim, string? seriesName, Settings cfg, bool dryRun)
    {
        if (sim is { IsRace: true, Moment: RaceMoment.Finished, Result: not null })
        {
            var key = $"{sim.SubSessionId}:{sim.SessionNum}";
            if (_posted.Contains(key))
                return;
            if (_pendingKey != key)
            {
                _pendingKey = key;
                _flagSeenUtc = DateTime.UtcNow;
            }
            _pending = sim;
            _pendingSeries = seriesName ?? sim.FallbackSeriesName;
            if (sim.SessionState >= SimState.StateCoolDown || DateTime.UtcNow - _flagSeenUtc > SettleTime)
                Flush(cfg, dryRun);
            return;
        }
        // Left the race (sim closed, or the next session started) before it settled.
        if (_pending != null)
            Flush(cfg, dryRun);
    }

    void Flush(Settings cfg, bool dryRun)
    {
        var sim = _pending!;
        _posted.Add(_pendingKey!);
        _pending = null;
        if (!cfg.PostsResults)
            return;

        var payload = Payload(sim, _pendingSeries);
        if (dryRun)
        {
            Log.Info($"Race result (dry run, not posted): {JsonSerializer.Serialize(payload, JsonOptions)}");
            return;
        }
        var url = cfg.ResultsWebhookUrl.Trim();
        _ = Task.Run(async () =>
        {
            var error = await PostAsync(url, payload);
            if (error == null)
                Log.Info($"Posted race result to Discord: {Headline(sim)}");
            else
                Log.Warn($"Couldn't post the race result: {error}");
        });
    }

    /// Sends a sample result, for the "Send test message" button. Returns null on success.
    public static Task<string?> SendTestAsync(string url) =>
        PostAsync(url.Trim(), Payload(DemoStates.FinishedSample(), "GT Sprint", test: true));

    static async Task<string?> PostAsync(string url, object payload)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), System.Text.Encoding.UTF8, "application/json");
            using var response = await AppInfo.Http.PostAsync(url, content);
            return response.IsSuccessStatusCode ? null : $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return e.Message;
        }
    }

    static string Headline(SimState sim)
    {
        var r = sim.Result!;
        var who = sim.DriverName.Length > 0 ? sim.DriverName : "Finished";
        var place = sim.Multiclass && r.ClassPosition > 0
            ? $"P{r.ClassPosition} in {(sim.CarClass.Length > 0 ? sim.CarClass : "class")} (P{r.Position} overall)"
            : $"P{r.Position} of {sim.FieldSize}";
        return sim.DriverName.Length > 0 ? $"🏁 {who} finished {place}" : $"🏁 Finished {place}";
    }

    static object Payload(SimState sim, string series, bool test = false)
    {
        var r = sim.Result!;
        var inv = CultureInfo.InvariantCulture;
        var track = sim.TrackConfig.Length > 0 ? $"{sim.TrackFull} - {sim.TrackConfig}" : sim.TrackFull;
        var fields = new List<object>
        {
            new { name = "Car", value = Or(sim.Car, "-"), inline = true },
            new { name = "Best lap", value = r.FastestTime > 0 ? TimeSpan.FromSeconds(r.FastestTime).ToString(@"m\:ss\.fff", inv) : "-", inline = true },
            new { name = "Laps", value = r.LapsComplete.ToString(inv), inline = true },
            new { name = "Incidents", value = $"{r.Incidents}x", inline = true },
        };
        if (sim.Team.Length > 0)
            fields.Add(new { name = "Team", value = sim.Team, inline = true });
        if (r.ReasonOut.Length > 0 && !r.ReasonOut.Equals("Running", StringComparison.OrdinalIgnoreCase))
            fields.Add(new { name = "Status", value = r.ReasonOut, inline = true });

        return new
        {
            username = "iRacing Presence",
            content = test ? "Test message from iRacing Discord Presence: race results will look like this." : null,
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = Headline(sim),
                    description = $"**{series}** · {sim.Session}\n{track}",
                    color = 0xE1232D,
                    fields,
                    thumbnail = sim.CarImage is { Length: > 0 } img ? new { url = img } : null,
                    footer = new { text = "iRacing Discord Presence" },
                    timestamp = DateTime.UtcNow.ToString("o", inv),
                },
            },
        };
    }

    static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}
