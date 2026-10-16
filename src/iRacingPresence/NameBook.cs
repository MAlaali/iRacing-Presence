using System.Text.Json;

namespace IRacingPresence;

/// names.json: display names for series and leagues, keyed like "series:4012" or "league:1234".
/// The iRacing SDK only exposes their numeric IDs, so names are filled in once by the user
/// (tray menu → "Name this series…") and remembered from then on.
sealed class NameBook(string path)
{
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly object _gate = new();
    SortedDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    DateTime _stamp = DateTime.MinValue;

    public string Path { get; } = path;

    public string? Get(string key)
    {
        lock (_gate)
        {
            Reload();
            return _names.TryGetValue(key, out var name) && name.Trim().Length > 0 ? name.Trim() : null;
        }
    }

    public void Set(string key, string name)
    {
        lock (_gate)
        {
            Reload();
            _names[key] = name.Trim();
            Save();
        }
    }

    /// Adds an empty entry for a series seen for the first time, so it's easy to fill in by hand.
    /// Returns true if the key was new.
    public bool Remember(string key)
    {
        lock (_gate)
        {
            Reload();
            if (!_names.TryAdd(key, ""))
                return false;
            Save();
            return true;
        }
    }

    void Reload()
    {
        try
        {
            if (!File.Exists(Path))
                return;
            var stamp = File.GetLastWriteTimeUtc(Path);
            if (stamp == _stamp)
                return;
            _stamp = stamp;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path), ReadOptions) ?? [];
            _names = new SortedDictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"names.json could not be read: {e.Message}");
        }
    }

    void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(_names, WriteOptions));
            _stamp = File.GetLastWriteTimeUtc(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"names.json could not be saved: {e.Message}");
        }
    }
}
