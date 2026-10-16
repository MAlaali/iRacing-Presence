using System.Text.Json;

namespace IRacingPresence;

/// Series and league names everyone shares, from names/series.json in the GitHub repository.
/// Used when you haven't named a series yourself, so one person naming "GT Sprint" is enough.
/// Cached in shared-names.json and refreshed twice a day.
sealed class SharedNames(string cachePath)
{
    const string Url = $"https://raw.githubusercontent.com/{AppInfo.Repo}/main/names/series.json";
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed record CacheFile(DateTime FetchedUtc, Dictionary<string, string> Names);

    readonly object _gate = new();
    Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    DateTime _fetchedUtc = DateTime.MinValue;
    DateTime _nextAttemptUtc = DateTime.MinValue;
    bool _loadedFromDisk;
    Task? _refresh;

    public string? Get(string key)
    {
        EnsureFresh();
        lock (_gate)
            return _names.TryGetValue(key, out var name) && name.Trim().Length > 0 ? name.Trim() : null;
    }

    void EnsureFresh()
    {
        lock (_gate)
        {
            if (!_loadedFromDisk)
            {
                _loadedFromDisk = true;
                LoadCache();
            }
            var now = DateTime.UtcNow;
            if (now - _fetchedUtc > MaxAge && now >= _nextAttemptUtc && _refresh is not { IsCompleted: false })
                _refresh = Task.Run(RefreshAsync);
        }
    }

    async Task RefreshAsync()
    {
        try
        {
            var json = await AppInfo.Http.GetStringAsync(Url);
            var names = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ReadOptions) ?? [];
            lock (_gate)
            {
                _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
                _fetchedUtc = DateTime.UtcNow;
            }
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheFile(_fetchedUtc, names)));
            Log.Info($"Loaded {names.Count(n => n.Value.Trim().Length > 0)} shared series names from GitHub.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            lock (_gate)
                _nextAttemptUtc = DateTime.UtcNow + RetryAfter;
            Log.Warn($"Couldn't load shared series names: {e.Message}");
        }
    }

    void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath));
            if (cache?.Names == null)
                return;
            _names = new Dictionary<string, string>(cache.Names, StringComparer.OrdinalIgnoreCase);
            _fetchedUtc = cache.FetchedUtc;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"shared-names.json could not be read: {e.Message}");
        }
    }
}
