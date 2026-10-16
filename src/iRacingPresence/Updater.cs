using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace IRacingPresence;

/// Checks GitHub for a newer release and installs it: downloads the matching setup, checks its
/// size and SHA-256 against what GitHub reports, and runs it with --install (which closes this
/// copy, replaces it and starts the new one).
static class Updater
{
    const string LatestUrl = $"https://api.github.com/repos/{AppInfo.Repo}/releases/latest";

    public sealed record Release(Version Version, string Tag, string PageUrl, string AssetName, string AssetUrl, long Size, string? Sha256);

    public static Version Current => Version.TryParse(Installer.Version, out var v) ? v : new Version(0, 0);

    /// The latest release if it's newer than this copy, otherwise null. Throws on network errors.
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await AppInfo.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= Current)
            return null;

        // Stay on the same kind of build: the lite one needs the .NET runtime, the full one doesn't.
        var wanted = AppInfo.Flavor == "lite" ? "iRacingPresence-Setup-lite.exe" : "iRacingPresence-Setup.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            string? sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:")
                ? d["sha256:".Length..]
                : null;
            return new Release(version, tag, root.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl,
                wanted, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    /// Downloads the setup to a fresh temp folder and verifies it. Returns its path.
    public static async Task<string> DownloadAsync(Release release, CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "iRacingPresence-update", release.Tag);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.AssetName);

        using (var response = await AppInfo.Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, ct);
        }

        var length = new FileInfo(path).Length;
        if (length != release.Size)
            throw new InvalidDataException($"the download is {length} bytes, GitHub says {release.Size}");
        if (release.Sha256 != null)
        {
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download's checksum doesn't match GitHub's");
        }
        return path;
    }

    /// Runs the downloaded setup silently. It closes this app as part of installing.
    public static void Install(string setupPath) =>
        Process.Start(new ProcessStartInfo(setupPath, "--install") { UseShellExecute = false })?.Dispose();
}
