using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace IRacingPresence;

/// The exe is its own installer. Run from anywhere else (e.g. Downloads), it offers to copy itself
/// to %LOCALAPPDATA%\iRacingPresence, start with Windows, add a Start menu shortcut, and add an
/// entry under Settings → Apps for uninstalling. Running a newer copy the same way updates it.
/// Everything is per user, so no administrator rights are needed.
static class Installer
{
    public const string AppName = "iRacing Discord Presence";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKeyRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    public sealed record Target(string Folder, string RegistryName, string DisplayName, string Shortcut)
    {
        public string Exe => Path.Combine(Folder, "iRacingPresence.exe");
    }

    public static Target Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iRacingPresence"),
        "iRacingPresence",
        AppName,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk"));

    /// Everything inside one folder, with registry names that can't clash with a real install.
    public static Target ForTesting(string folder) => new(
        folder, "iRacingPresence-Test", $"{AppName} (test)", Path.Combine(folder, $"{AppName}.lnk"));

    public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// The "Start with Windows" entry, pointing at whichever copy turns it on.
    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(Default.RegistryName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value)
                key.SetValue(Default.RegistryName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(Default.RegistryName, throwOnMissingValue: false);
        }
    }

    public static bool IsRunningFrom(Target target) =>
        string.Equals(Path.GetDirectoryName(Environment.ProcessPath), target.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// Asks before installing or updating. Returns true when it installed.
    public static bool Offer(Target target)
    {
        string question;
        if (File.Exists(target.Exe))
        {
            var installed = FileVersionInfo.GetVersionInfo(target.Exe).ProductVersion?.Split('+')[0] ?? "unknown";
            question = $"{AppName} {installed} is already installed.\n\n" +
                       $"Replace it with version {Version}? Your settings and series names are kept.";
        }
        else
        {
            question = $"Install {AppName} {Version}?\n\n" +
                       "It shows what you're doing in iRacing (series, session, car, track and time left) on your Discord profile.\n\n" +
                       "It starts with Windows and runs quietly in the notification area: look for the \"iR\" icon near the clock.";
        }
        if (MessageBox.Show(question, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return false;

        try
        {
            Install(target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Installing failed:\n{e.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public static void Install(Target target)
    {
        StopRunningCopies();
        Directory.CreateDirectory(target.Folder);
        var source = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target.Exe), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, target.Exe, overwrite: true);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            run.SetValue(target.RegistryName, $"\"{target.Exe}\"");

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyRoot + target.RegistryName))
        {
            key.SetValue("DisplayName", target.DisplayName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", AppName);
            key.SetValue("DisplayIcon", $"\"{target.Exe}\"");
            key.SetValue("InstallLocation", target.Folder);
            key.SetValue("UninstallString", $"\"{target.Exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{target.Exe}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(target.Exe).Length / 1024), RegistryValueKind.DWord);
        }

        CreateShortcut(target.Shortcut, target.Exe);
        Log.Info($"Installed version {Version} to {target.Folder}.");
    }

    public static void Launch(Target target, params string[] args) =>
        Process.Start(new ProcessStartInfo(target.Exe, args) { WorkingDirectory = target.Folder })?.Dispose();

    public static void Uninstall(Target target, bool quiet)
    {
        if (!quiet && MessageBox.Show(
                $"Uninstall {AppName}?\n\nThis removes the app and its settings from this PC and clears your iRacing status on Discord.",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        StopRunningCopies();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            run?.DeleteValue(target.RegistryName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyRoot + target.RegistryName, throwOnMissingSubKey: false);
        try
        {
            File.Delete(target.Shortcut);
        }
        catch (IOException) { }

        // This exe may be the one being removed and can't delete itself while running, so a
        // short-lived cmd deletes the folder a couple of seconds after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{target.Folder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })?.Dispose();

        if (!quiet)
            MessageBox.Show($"{AppName} has been uninstalled.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// Closes the running tray app (if any) so its exe can be replaced. Discord clears the status
    /// as soon as its connection closes, and the new copy sets it again.
    static void StopRunningCopies()
    {
        foreach (var process in Process.GetProcessesByName("iRacingPresence"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    static void CreateShortcut(string path, string target)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Shows your iRacing sessions on Discord";
            link.Save();
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't create the Start menu shortcut: {e.Message}");
        }
    }
}
