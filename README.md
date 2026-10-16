# iRacing Discord Presence

**[⬇ Download the latest version](https://github.com/MAlaali/iRacing-Presence/releases/latest)**: get
`iRacingPresence-Setup.exe`, double-click it and click *Yes*. Steps are in
[How to install.txt](release/How%20to%20install.txt).

A small Windows tray app that shows what you're doing in iRacing on your Discord profile:

```
Playing iRacing
  GT Sprint · Race
  Porsche 911 GT3 R (992) @ Spa  (3 of 24)
  12:34 left
```

| Shown | Where it comes from |
|---|---|
| Track | iRacing SDK (`TrackDisplayShortName`, full name + layout on image hover) |
| Series (GT Sprint, IMSA, …) | Your saved name for the series ID (see [Series names](#series-names)) |
| Session (Practice / Qualifying / Race / Warmup / Heat…) | iRacing SDK, current session |
| Time left | Live countdown from `SessionTimeRemain`. Lap races show `Lap 7/20` instead |
| Car | Your car's `CarScreenName` |
| Position | `(3 of 24)`, your class position in multiclass races |
| Pictures | Official picture of your car (big) and track (round badge), matched by name from iracing.com's public car and track lists; cached a week in `image-cache.json` |

It reads iRacing's official telemetry shared memory, the same source every overlay uses. It doesn't touch the sim itself and needs no login.

## Installing

Double-click `release\iRacingPresence-Setup.exe` **in File Explorer** and click *Yes*. It installs itself
to `%LOCALAPPDATA%\iRacingPresence`, starts with Windows, adds a Start menu shortcut and an entry
under *Settings → Apps* for uninstalling, and starts. No administrator rights are needed. Running a
newer setup the same way updates it and keeps your settings.

Install from File Explorer, not from a terminal inside the Claude app. The Claude app runs in a
Windows package sandbox that redirects files written under `AppData` into its own storage, where
Windows can't find them at login.

To share it, send friends the files in `release\` (`How to install.txt` is written for them). The
Discord application ID (`1560590131432398900`, named "iRacing") is built in, so they need no setup.
Everyone's status goes through that application, so don't delete it in the Developer Portal. An art
asset uploaded there as `iracing` becomes the fallback picture for everyone.

In Discord, *User Settings → Activity Privacy → Share my activity* must be on.

To check the Discord side without starting iRacing, exit the tray app and run it once with demo data:

```bash
"%LOCALAPPDATA%/iRacingPresence/iRacingPresence.exe" --demo
```

Your Discord profile cycles through a fake practice, qualifying and race.

## Series names

The iRacing SDK only exposes a series **ID**, not its name. The first time you join a new series, a
notification pops up. Right-click the tray icon → **Name this series…**, type e.g. `GT Sprint`, and
it's remembered from then on in `names.json`. Leagues work the same way. Until then, the status shows
"iRacing Official", "Hosted Session", "League Session", "Test Drive" or "AI Session".

## Tray menu

- **Name this series…**: shown while you're in an official or league session
- **Hide my iRacing status**: pause or resume without closing the app
- **Open settings / series names / log**
- **Start with Windows**

Icon colours: grey = waiting for iRacing, green = showing on Discord, blue = hidden, red = needs attention (hover or open the menu to see why).

## Customising

Everything lives in `settings.json` next to the exe; it's commented. The text lines are templates:

```jsonc
"details": "{series} · {session} · {progress}",
"state":   "{car} @ {track}",
```

Placeholders: `{series} {session} {track} {track_full} {track_config} {track_country} {car} {car_short}
{car_class} {car_number} {status} {position} {class_position} {field_size} {class_field_size} {lap}
{laps_total} {laps} {progress} {time_left} {incidents} {best_lap} {irating} {license}`.
Empty placeholders are removed along with their ` · ` / ` @ ` separators.

Pictures: `"largeImage": "{car_image}|iracing"` and `"smallImage": "{track_image}"` by default. Swap
them to make the track the big picture. `a|b` means "a, or b if a is empty"; `iracing` is the
art asset you can upload as a fallback for cars with no picture.

Other options: `activityType` (`"competing"` → "Competing in iRacing"), `memberListShows`
(`"details"` puts e.g. "GT Sprint · Race" in the member list instead of "iRacing"), images, up to two
link buttons, and the idle text shown while you're in the iRacing menus.

## Troubleshooting

- **Two iRacing entries on your profile**: Discord's own game detection adds a second, plain one.
  Remove iRacing under *User Settings → Registered Games*.
- **Nothing shows up**: open the tray menu; the first line says what's wrong. Rich Presence needs the
  Discord **desktop** app (not the browser), running as the same Windows user. If Discord runs as
  administrator, run this app as administrator too, or neither.
- **Wrong or missing series name**: edit `names.json` (keys look like `"series:4012"`).
- The log (`iracing-presence.log`) records every presence change.

## Building

Requires the .NET 9 SDK.

```bash
powershell -File build.ps1
```

This creates `dist\iRacingPresence.exe` (≈0.3 MB, needs the .NET 9 Desktop Runtime). To build the
files to share:

```bash
powershell -File build.ps1 -Release
```

This creates `release\iRacingPresence-Setup.exe` (≈48 MB, runs on any 64-bit Windows PC) and
`release\iRacingPresence-Setup-lite.exe` (≈0.3 MB, small enough for a Discord chat; Windows offers
to install the .NET runtime on first launch). Bump `<Version>` in the `.csproj` for each release so
the installer can tell it's an update. `tools\make-icon.ps1` redraws the exe icon.

Installer flags (see `Installer.cs`): `--install [--no-launch]`, `--uninstall [--quiet]`, and
`--install-test-root DIR` to try either one in a throwaway folder with separate registry names.

### Testing without iRacing

`tools/FakeSim` writes iRacing's shared-memory layout under a test name, so the real tools you have
open (Garage61, Trading Paints, …) never see the fake session:

```bash
dotnet run --project tools/FakeSim -- weekend 60
```

```bash
src/iRacingPresence/bin/Debug/net9.0-windows/iRacingPresence.exe --dry-run --memmap Local\IRSDKMemMapFileName.FakeSim
```

`--dry-run` writes the presence to the log instead of Discord. Scenarios: `weekend`, `lap-race`, `crash`.

`tools/FakeDiscord` stands in for Discord's pipe and goes down and up on a timer, to test that the
app reconnects after Discord restarts. Start the app with `--discord-pipe <name>` to use it.

## How it works

| File | Role |
|---|---|
| `IRacing/IRacingReader.cs` | Reads the SDK shared memory (`Local\IRSDKMemMapFileName`): header, variable table, latest telemetry buffer, session YAML |
| `IRacing/SessionYaml.cs` | Small tolerant parser for iRacing's session YAML |
| `IRacing/SimState.cs` | Pulls track, series, session, timing, car and position out of a snapshot |
| `PresenceBuilder.cs` | Fills the templates and builds the Discord activity |
| `ImageCatalog.cs` | Matches car and track names to iracing.com pictures |
| `Discord/DiscordIpc.cs` | Talks to Discord over its local `discord-ipc-N` pipe (no packages needed) |
| `PresenceService.cs` | 2-second loop; sends when something visible changes (Discord allows ~1 update / 4 s), and re-sends every 20 s so a Discord restart is noticed and the status comes back |
| `TrayApp.cs` | Tray icon and menu |
| `Installer.cs` | Self-install, update and uninstall (per user: folder, startup entry, Start menu shortcut, Settings → Apps entry) |
