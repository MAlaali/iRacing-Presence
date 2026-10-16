using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IRacingPresence;

/// Finds a picture of a car or track by the name the sim reports. The pictures come from
/// iracing.com's public car and track lists, whose tiles are labelled with the same names
/// (data-name="Porsche 911 GT3 R (992)"). Discord shows them through its image proxy.
/// The lists are cached in image-cache.json and refreshed weekly.
sealed partial class ImageCatalog(string cachePath)
{
    const string CarsUrl = "https://www.iracing.com/cars/";
    const string TracksUrl = "https://www.iracing.com/tracks/";
    const double MinFuzzyScore = 0.6;
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly object _gate = new();
    List<Entry> _cars = [];
    List<Entry> _tracks = [];
    DateTime _fetchedUtc = DateTime.MinValue;
    DateTime _nextAttemptUtc = DateTime.MinValue;
    Task? _refresh;
    bool _loadedFromDisk;
    readonly HashSet<string> _reportedMisses = new(StringComparer.OrdinalIgnoreCase);

    sealed record Entry(string Name, string Url);
    sealed record CacheFile(DateTime FetchedUtc, List<Entry> Cars, List<Entry> Tracks);

    public string? Car(string name) => Find(name, cars: true);
    public string? Track(string name) => Find(name, cars: false);

    string? Find(string name, bool cars)
    {
        if (name.Trim().Length == 0)
            return null;
        EnsureFresh();
        List<Entry> entries;
        bool ready;
        lock (_gate)
        {
            entries = cars ? _cars : _tracks;
            ready = entries.Count > 0;
        }
        if (!ready)
            return null;

        var url = BestMatch(entries, name);
        if (url == null && _reportedMisses.Add(name))
            Log.Info($"No iracing.com picture matches the {(cars ? "car" : "track")} \"{name}\".");
        return url;
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
            bool stale = now - _fetchedUtc > MaxAge || _cars.Count == 0 || _tracks.Count == 0;
            if (stale && now >= _nextAttemptUtc && _refresh is not { IsCompleted: false })
                _refresh = Task.Run(RefreshAsync);
        }
    }

    async Task RefreshAsync()
    {
        try
        {
            var cars = Parse(await Http.GetStringAsync(CarsUrl));
            var tracks = Parse(await Http.GetStringAsync(TracksUrl));
            if (cars.Count == 0 || tracks.Count == 0)
                throw new InvalidDataException("the car or track list came back empty (the page layout may have changed)");

            lock (_gate)
            {
                _cars = cars;
                _tracks = tracks;
                _fetchedUtc = DateTime.UtcNow;
            }
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheFile(_fetchedUtc, cars, tracks), JsonOptions));
            Log.Info($"Loaded pictures for {cars.Count} cars and {tracks.Count} tracks from iracing.com.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            lock (_gate)
                _nextAttemptUtc = DateTime.UtcNow + RetryAfter;
            Log.Warn($"Couldn't load car and track pictures from iracing.com: {e.Message}");
        }
    }

    void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath));
            if (cache == null)
                return;
            _cars = cache.Cars ?? [];
            _tracks = cache.Tracks ?? [];
            _fetchedUtc = cache.FetchedUtc;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"image-cache.json could not be read, it will be downloaded again: {e.Message}");
        }
    }

    /// Each tile on the list pages starts with data-name="…" and contains one picture.
    static List<Entry> Parse(string html)
    {
        var entries = new List<Entry>();
        var tiles = html.Split("data-name=\"");
        foreach (var tile in tiles.Skip(1))
        {
            int quote = tile.IndexOf('"');
            if (quote <= 0)
                continue;
            var name = WebUtility.HtmlDecode(tile[..quote]).Trim();
            var img = ImgSrcPattern().Match(tile);
            if (name.Length == 0 || !img.Success)
                continue;
            var url = WebUtility.HtmlDecode(img.Groups[1].Value);
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && url.Length <= 256)
                entries.Add(new Entry(name, url));
        }
        return entries;
    }

    /// Exact name first, then the closest name by shared words (handles "BMW M4 G82 GT4" vs
    /// "BMW M4 GT4", or "[Legacy]" prefixes).
    static string? BestMatch(List<Entry> entries, string name)
    {
        var wanted = Words(name);
        if (wanted.Count == 0)
            return null;
        var key = string.Join(' ', wanted);

        string? best = null;
        double bestScore = 0;
        foreach (var entry in entries)
        {
            var words = Words(entry.Name);
            if (string.Join(' ', words) == key)
                return entry.Url;
            int shared = words.Count(wanted.Contains);
            double score = (double)shared / (words.Count + wanted.Count - shared);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry.Url;
            }
        }
        return bestScore >= MinFuzzyScore ? best : null;
    }

    static List<string> Words(string name) =>
        NonWordPattern().Split(WebUtility.HtmlDecode(name).ToLowerInvariant())
            .Where(w => w.Length > 0 && w != "legacy")
            .Distinct()
            .ToList();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("iRacingPresence/1.0 (Discord status app)");
        return client;
    }

    [GeneratedRegex("<img[^>]+?src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWordPattern();
}
