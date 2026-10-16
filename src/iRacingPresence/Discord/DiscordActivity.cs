using System.Text.Json;
using System.Text.Json.Serialization;

namespace IRacingPresence.Discord;

/// The Rich Presence activity object sent with SET_ACTIVITY.
sealed class DiscordActivity
{
    /// 0 Playing, 2 Listening, 3 Watching, 5 Competing.
    [JsonPropertyName("type")] public int Type { get; set; }
    /// What the member list shows after "Playing": 0 app name, 1 state, 2 details.
    [JsonPropertyName("status_display_type")] public int? StatusDisplayType { get; set; }
    [JsonPropertyName("details")] public string? Details { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("timestamps")] public ActivityTimestamps? Timestamps { get; set; }
    [JsonPropertyName("assets")] public ActivityAssets? Assets { get; set; }
    [JsonPropertyName("party")] public ActivityParty? Party { get; set; }
    [JsonPropertyName("buttons")] public List<ActivityButton>? Buttons { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// True when nothing visible would change. Timers count as unchanged unless they move by
    /// more than a few seconds, so telemetry jitter doesn't burn Discord's rate limit.
    public static bool LooksTheSame(DiscordActivity? a, DiscordActivity? b)
    {
        if (a == null || b == null)
            return a == b;
        if (!Near(a.Timestamps?.Start, b.Timestamps?.Start) || !Near(a.Timestamps?.End, b.Timestamps?.End))
            return false;
        var ta = a.Timestamps;
        var tb = b.Timestamps;
        a.Timestamps = b.Timestamps = null;
        bool same = a.ToJson() == b.ToJson();
        a.Timestamps = ta;
        b.Timestamps = tb;
        return same;
    }

    static bool Near(long? x, long? y) =>
        x.HasValue == y.HasValue && (!x.HasValue || Math.Abs(x.Value - y!.Value) < 10_000);
}

sealed class ActivityTimestamps
{
    /// Unix time in milliseconds. Discord shows "elapsed" from start, or a countdown to end.
    [JsonPropertyName("start")] public long? Start { get; set; }
    [JsonPropertyName("end")] public long? End { get; set; }
}

sealed class ActivityAssets
{
    [JsonPropertyName("large_image")] public string? LargeImage { get; set; }
    [JsonPropertyName("large_text")] public string? LargeText { get; set; }
    [JsonPropertyName("small_image")] public string? SmallImage { get; set; }
    [JsonPropertyName("small_text")] public string? SmallText { get; set; }
}

sealed class ActivityParty
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    /// [current, max]; Discord renders it as "(3 of 24)" after the state line.
    [JsonPropertyName("size")] public int[]? Size { get; set; }
}

sealed class ActivityButton
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}
