namespace IRacingPresence;

static class Program
{
    /// Flags:
    ///   --demo            show fake sessions on Discord (no iRacing needed)
    ///   --dry-run         read iRacing but only write the presence to the log, never to Discord
    ///   --data-dir PATH   where settings.json, names.json and the log live (default: next to the exe)
    ///   --memmap NAME     read another shared-memory name (used with tools/FakeSim for testing)
    ///   --discord-pipe N  talk to this pipe instead of Discord's (used with tools/FakeDiscord)
    ///
    /// Installing (see Installer):
    ///   (no arguments, run from outside the install folder)   ask, then install or update
    ///   --install [--no-launch]   install or update without asking
    ///   --uninstall [--quiet]     remove the app, its settings and its startup entry
    ///   --install-test-root DIR   do the above in DIR instead, for testing
    [STAThread]
    static int Main(string[] args)
    {
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        var dataDir = Option(args, "--data-dir") is { } dir ? Path.GetFullPath(dir) : AppContext.BaseDirectory;
        var memMapName = Option(args, "--memmap") ?? IRacing.IRacingReader.MemMapName;
        Directory.CreateDirectory(dataDir);

        var logPath = Path.Combine(dataDir, "iracing-presence.log");
        Log.Init(logPath);

        var target = Option(args, "--install-test-root") is { } testRoot
            ? Installer.ForTesting(Path.GetFullPath(testRoot))
            : Installer.Default;
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Installer.Uninstall(target, quiet: args.Contains("--quiet", StringComparer.OrdinalIgnoreCase));
            return 0;
        }
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            Installer.Install(target);
            if (!args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase))
                Installer.Launch(target, "--welcome");
            return 0;
        }
        if (args.Length == 0 && !Installer.IsRunningFrom(target))
        {
            if (Installer.Offer(target))
                Installer.Launch(target, "--welcome");
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\iRacingPresence.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            if (!dryRun)
                MessageBox.Show("iRacing Presence is already running. Look for the \"iR\" icon in the notification area.",
                    "iRacing Presence", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        // A failing tray-menu action shouldn't close the app; log it and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error($"Unexpected error in the tray: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Fatal error: {e.ExceptionObject}");
        var settings = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        var names = new NameBook(Path.Combine(dataDir, "names.json"));
        var images = new ImageCatalog(Path.Combine(dataDir, "image-cache.json"));
        var options = new RunOptions(demo, dryRun, memMapName, Option(args, "--discord-pipe"),
            Welcome: args.Contains("--welcome", StringComparer.OrdinalIgnoreCase));
        Application.Run(new TrayApp(settings, names, images, logPath, options));
        return 0;
    }

    static string? Option(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
