# iRacing Discord Presence

**[⬇ Download the latest version](https://github.com/MAlaali/iRacing-Presence/releases/latest)**: get
`iRacingPresence-Setup.exe`, double-click it and click *Yes*. Steps are in
[How to install.txt](release/How%20to%20install.txt).

A small Windows tray app that shows what you're doing in iRacing on your Discord profile:

```
Playing iRacing
  GT Sprint · Race · Last lap · P3 of 24
  Porsche 911 GT3 R (992) @ Spa
  01:34 left
```

| Shown | Where it comes from |
|---|---|
| Series (GT Sprint, IMSA, …) | Your name for the series ID, or the [shared list](#series-names) |
| Session (Practice / Qualifying / Race / Heat…) | iRacing SDK, current session |
| Race moments | *On the grid*, *Formation lap*, *Caution*, *Red flag*, *Last lap*, *🏁 Finished*, *In the pits*, *In the garage*, *Spectating*, from the session state and the flags you're shown |
| Position | `P3 of 24` (`P2 of 12 in GT3` in multiclass), the official result after the flag, or `24 drivers` before you have a position |
| Time left | Live countdown. Lap races show `Lap 7/20` |
| Car and track | Your car and the track, with their official iracing.com pictures (big picture and round badge) |
| Hover the car | `Porsche 911 GT3 R (992) · Team · 2450 iR · B 2.87` |
| Hover the track | `Circuit de Spa-Francorchamps - Grand Prix Pits · Rain · Night · 14°C · Best 2:17.512` |
| Spectating | `Watching Max Verstappen @ Spa`, with their car's picture |

It reads iRacing's official telemetry shared memory, the same source every overlay uses. It doesn't touch the sim itself and needs no login.

**Also:**
- **Race results to Discord.** After each race it can post `🏁 YourName finished P3 of 24` with your car, best lap, laps and incidents to a channel, through a webhook. Off until you paste a webhook URL.
- **Auto-update.** It checks GitHub every few hours and offers to install new versions with one click; the download's size and SHA-256 are checked against GitHub's before it runs.
- **Settings window** with a live preview of your Discord card (right-click the tray icon › *Settings…*).
- Reconnects by itself when Discord restarts, and starts with Windows.

## Installing

Double-click `iRacingPresence-Setup.exe` **in File Explorer** and click *Yes*. It installs itself to
`%LOCALAPPDATA%\iRacingPresence`, starts with Windows, adds a Start menu shortcut and an entry under
*Settings → Apps* for uninstalling, and starts. No administrator rights are needed. Running a newer
setup updates it and keeps your settings (or just accept the update offer from the tray).

Install from File Explorer, not from a terminal inside the Claude app. The Claude app runs in a
Windows package sandbox that redirects files written under `AppData` into its own storage, where
Windows can't find them at login.

The Discord application ID (`1560590131432398900`, named "iRacing") is built in, so nobody needs any
setup. Everyone's status goes through that application, so don't delete it in the Developer Portal.

In Discord, *User Settings → Activity Privacy → Share my activity* must be on.

To check the Discord side without starting iRacing, exit the tray app and run it once with demo data:

```bash
"%LOCALAPPDATA%/iRacingPresence/iRacingPresence.exe" --demo
```

## Series names

The iRacing SDK only exposes a series **ID**, not its name. Names come from, in order:

1. **Yours**: right-click the tray icon → **Name this series…** (saved in `names.json`).
2. **The shared list**: [`names/series.json`](names/series.json) in this repository. Every copy of
   the app downloads it twice a day, so adding `"series:4012": "GT Sprint"` there names it for
   everybody.
3. Otherwise: "iRacing Official", "Hosted Session", "League Session", "Test Drive" or "AI Session".

iRacing's online data service would have the real names, but it now requires an approved developer
account, and iRacing has [paused new registrations](https://support.iracing.com/support/solutions/articles/31000177790-oauth-client-credentials).

## Race results to a Discord channel

In Discord: *Server Settings › Integrations › Webhooks › New Webhook*, pick the channel, *Copy
Webhook URL*. Paste it under *Settings… › Extras › Results webhook* and press *Send test message*.
After each race (once the session cools down, 90 s after the flag, or when you leave) it posts one
message with your finishing position, car, best lap, laps and incidents.

## Tray menu

- **Settings…** (or double-click the icon): every option, with a live preview
- **Update to x.y.z…**: shown when a new version is out
- **Name this series…**: shown while you're in an official or league session
- **Hide my iRacing status**: pause or resume without closing the app
- **Start with Windows**
- **Open series names / settings file / log**
- **Version · check for updates**

Icon colours: grey = waiting for iRacing, green = showing on Discord, blue = hidden, red = needs attention (hover or open the menu to see why).

## Customising

The settings window covers everything; `settings.json` (commented) holds the same values. The text
lines are templates:

```jsonc
"details": "{series} · {session} · {moment} · {progress} · {standing}",
"state":   "{car} @ {track}",
"spectateState": "{watching} @ {track}",
"largeImageText": "{car} · {team} · {rating} · {license}",
"smallImageText": "{track_full} · {conditions} · {best}",
```

Placeholders: `{series} {session} {moment} {standing} {players} {progress} {track} {track_full}
{track_config} {track_country} {car} {car_short} {car_class} {car_number} {team} {driver} {rating}
{license} {best} {conditions} {watching} {watching_car} {position} {class_position} {field_size}
{class_field_size} {lap} {laps_total} {laps} {time_left} {incidents} {best_lap} {irating} {status}`.
Empty placeholders are removed along with their ` · ` / ` @ ` separators.

Pictures: `"largeImage": "{car_image}|iracing"` and `"smallImage": "{track_image}"`. Swap them to
make the track the big picture. `a|b` means "a, or b if a is empty". Other options: `activityType`
(`"competing"`), `memberListShows`, `temperatureUnit` (`"C"`/`"F"`), up to two link buttons, and the
text shown while you're in the iRacing menus.

## Troubleshooting

- **Two iRacing entries on your profile**: Discord's own game detection adds a second, plain one.
  Remove iRacing under *User Settings → Registered Games*.
- **Nothing shows up**: open the tray menu; the first line says what's wrong. Rich Presence needs the
  Discord **desktop** app (not the browser), running as the same Windows user.
- **"Windows protected your PC"** when installing: the app isn't code-signed (that needs a paid
  certificate). Click *More info → Run anyway*.
- The log (`iracing-presence.log`) records every presence change and what went wrong.

## Building

Requires the .NET 9 SDK.

```bash
powershell -File build.ps1 -Release
```

This creates `release\iRacingPresence-Setup.exe` (≈48 MB, runs on any 64-bit Windows PC) and
`release\iRacingPresence-Setup-lite.exe` (≈0.3 MB, needs the .NET 9 Desktop Runtime; Windows offers
to install it). Without `-Release` it builds only `dist\iRacingPresence.exe`. Bump `<Version>` in the
`.csproj` for each release; publishing a GitHub release with both setups is what the auto-update
picks up. `winget\` holds the manifests for listing it in winget.

Flags: `--install [--no-launch]`, `--uninstall [--quiet]`, `--install-test-root DIR` (try either in a
throwaway folder), `--demo`, `--dry-run`, `--check-update` (check and download, don't install),
`--render-settings PREFIX` (draw the settings window's tabs to PNGs).

### Testing without iRacing

`tools/FakeSim` writes iRacing's shared-memory layout under a test name, so the real tools you have
open (Garage61, Trading Paints, …) never see the fake session:

```bash
dotnet run --project tools/FakeSim -- finish 42
```

```bash
src/iRacingPresence/bin/Debug/net9.0-windows/iRacingPresence.exe --dry-run --memmap Local\IRSDKMemMapFileName.FakeSim
```

`--dry-run` writes the presence (and race results) to the log instead of Discord. Scenarios:
`weekend`, `lap-race`, `crash`, `finish`, `spectate`, `night-rain`. `tools/FakeDiscord` stands in for
Discord's pipe and goes down and up on a timer; start the app with `--discord-pipe <name>` to use it.

## How it works

| File | Role |
|---|---|
| `IRacing/IRacingReader.cs` | Reads the SDK shared memory: header, variable table, latest telemetry buffer, session YAML |
| `IRacing/SessionYaml.cs` | Small tolerant parser for iRacing's session YAML |
| `IRacing/SimState.cs` | Track, series, session, race moment, timing, car, position, results, weather, spectating |
| `PresenceBuilder.cs` | Fills the templates and builds the Discord activity |
| `ImageCatalog.cs` | Matches car and track names to iracing.com pictures |
| `SharedNames.cs` | Downloads the shared series names |
| `ResultsReporter.cs` | Posts race results to a Discord webhook |
| `Updater.cs` | Checks GitHub releases, downloads and verifies the setup, runs it |
| `Discord/DiscordIpc.cs` | Talks to Discord over its local `discord-ipc-N` pipe (no packages needed) |
| `PresenceService.cs` | 2-second loop; sends when something visible changes, re-sends every 20 s so a Discord restart is noticed |
| `TrayApp.cs`, `SettingsForm.cs` | Tray icon and menu; settings window with the live preview |
| `Installer.cs` | Self-install, update and uninstall (per user) |
