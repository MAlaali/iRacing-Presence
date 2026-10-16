namespace IRacingPresence;

/// Appends timestamped lines to iracing-presence.log next to the settings file.
static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Delete(path);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                if (_path != null)
                    File.AppendAllText(_path, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
