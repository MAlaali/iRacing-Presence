using System.Reflection;

namespace IRacingPresence;

/// Where the app lives online, and the one HttpClient it shares.
static class AppInfo
{
    public const string Repo = "MAlaali/iRacing-Presence";
    public const string RepoUrl = $"https://github.com/{Repo}";

    public static readonly HttpClient Http = CreateClient();

    /// "full" (standalone setup), "lite" (needs the .NET runtime) or "dev"; set by build.ps1.
    public static string Flavor { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "Flavor")?.Value ?? "dev";

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"iRacingPresence/{Installer.Version} (+{RepoUrl})");
        return client;
    }
}
