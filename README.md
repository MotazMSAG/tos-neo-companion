# TOS NEO Companion

A Windows tray helper for **Tree of Savior: NEO** (PC client). It was built for one player's own use and is shared here
in case it helps someone else.

> **No longer maintained.** The author has stopped playing. Game updates will change the screens it reads, so parts
> may stop working until someone re-cuts the screen pictures (see [Maintaining it](#maintaining-it)). Issues and
> pull requests are welcome, but nobody is actively working on it.

**Just want to use it?** Jump to [Quick start for players](#quick-start-for-players).

## What it does

| Part | What it is for |
|---|---|
| **Today** (daily checklist) | Ticks your dailies by itself when the game's own log shows them done: arena, gear rift, orders, Hugo train, demon trial, Kanu's Wish, hyperweather boxes and more. Counted items show their progress (Gear rift 3/4). Shows only the items for the current weekday, timed events with a countdown, and Homestead timers. It **only reads the log: it never clicks**. |
| **Crafting** | Pick a recipe and an amount: it gathers the missing materials with the game's own Gathering Helper, then crafts through the Make tab. Includes a queue, "Gather everything" (refills every raw material your recipes use) and "keep in stock" rules. |
| **Kitty Tea House helper** | Shows the best square for each piece in the Kitty Tea House minigame. It learns piece points as you play. An optional auto-play presses the moves for you. |
| **Activity / Stats** | Plain-language history of what it did, plus a 7-day summary. |

See [docs/MANUAL.md](docs/MANUAL.md) for the full user manual.

## How it works, and what it never does

It looks at the game window with a screen capture (`PrintWindow`) and reads the game's own text log
(`<game folder>\Log\log.txt`). It "clicks" by sending window messages to the game. To press a spot it briefly slides
the game window under your resting mouse, then slides it back.

It **never**:

- moves your mouse cursor or brings the game to the front;
- reads game memory or game files, injects code, or sends its own network traffic;
- runs on an emulator or VM;
- presses shop, gacha, purchase, Expand Inventory or Obtain Lumi buttons;
- logs in for you, or stores chat, other players' names or telemetry.

It works at a human pace (rate limits are enforced in code), and it stops for safety when anything unexpected
happens. A safety stop is never cleared by itself. These rules are checked by tests (`StaticRulesTests` scans the source
for forbidden Windows APIs). The full design is in [SPEC.md](SPEC.md).

## ⚠️ Use at your own risk

This is an **unofficial** fan project. It is not made, endorsed or supported by the game's developer or publisher.
Automating a game may break its terms of service. The author read the terms as allowing personal, human-paced
helpers, but **you** are responsible for checking the current terms for your region and account. Any account action is
your risk. The Today checklist and the Tea House advice (without auto-play) only read and never press anything. They
are the safest parts.

## Quick start for players

No programming tools needed.

**1. Check your setup.**

- Windows 10 or 11, 64-bit, and an account that may approve admin prompts.
- **Windows display scaling at 100%**: Settings > System > Display > Scale = **100%**. At 125% or 150% the
  companion waits forever with "display mode wrong".
- The game in **windowed mode, maximized, on a 1920×1080 monitor**, **English** client. The screen pictures it
  matches were cut at exactly this size.

**2. Download.** Open the [**Releases** page](https://github.com/MotazMSAG/tos-neo-companion/releases/latest) and
download `TOS-NEO-Companion-…-win-x64.zip` (under "Assets"). Unzip it to a folder of its own, for example
`C:\TOS-companion`.

**3. Tell it where the game is** (skip this if your game is in `D:\Games\TOS`).

1. Press **Win+R**, paste `rundll32 sysdm.cpl,EditEnvironmentVariables` and press Enter.
2. Under "User variables" click **New...**.
3. Variable name: `TOS_GAME_DIR`. Variable value: your game folder, the one that holds `TOS.exe` and the `Log`
   folder (for example `C:\Games\TOS`). Click OK twice.

**4. Your time zone** (skip this if your PC is set to UTC+3). See [Your time zone](#your-time-zone) below. Do it
**before** step 6, because the app checks that its files have not changed since you trusted them.

**5. Start it.** Double-click `Companion.App.exe`.

- If Windows says *"Windows protected your PC"*, click **More info**, then **Run anyway**. The app is not
  code-signed.
- Click **Yes** on the admin prompt. It asks on every start.
- A tray icon appears near the clock (look under the **^** arrow).

**6. Trust the build once.** Right-click the tray icon > **Advanced** > **Trust this build...**. Nothing runs
before that.

**7. Use it.**

- **Today (daily checklist):** left-click the tray icon. The window opens on the Today tab. It works right
  away: it only reads the game's log and never clicks. Start the game and play: dailies tick by themselves.
- **Kitty Tea House helper:** tray menu > **Kitty Tea House helper...** while the minigame is open in the game.
- **Crafting automation (optional):**
  1. Right-click the tray icon > **Advanced** > **Automation** > **Crafting** (turn it on).
  2. With the game running, press **Scan all recipes** once in the companion's **Craft** tab. It opens the
     crafting window itself and reads every recipe (5 to 20 minutes).
  3. Choose a recipe and an amount, then **Start crafting**.
  4. While it works, rest your mouse on another window (a browser, Notepad) in front of the game, and keep
     your hands off the mouse. See the [manual](docs/MANUAL.md) for the details.
- **Stop at once:** **Ctrl+Alt+Pause** anywhere, or tray > **Stop now**.

**Uninstall:** tray > **Exit**, delete the folder, then delete `C:\ProgramData\TOS-companion`. Windows asks for
admin rights.

Everything the app shows and every message is explained in the [user manual](docs/MANUAL.md).

## Your time zone

The daily checklist decides when a new day starts by the clock. Its times are written for a PC set to
**UTC+3** (the author's):

- daily reset at 07:00;
- calendar day at 02:00;
- weekly reset on Monday at 07:00;
- event times such as the guild raccoons and the world boss.

If your PC uses another time zone, shift these times by the difference:

1. Open `data\dailies.json` (in the unzipped folder) with Notepad.
2. Change the `"resets"` line near the top, and every `"at": "HH:MM"` value further down.
   - Example: your PC is UTC+1, so subtract 2 hours. `"daily": "07:00:00"` becomes `"05:00:00"`, and
     `"at": "21:44"` becomes `"19:44"`.
3. Save. If you already trusted the build, choose **Advanced > Trust this build...** again.

Players on another server region may also have different reset times. Watch when the in-game dailies reset and
use that time.

## Build from source (developers)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/MotazMSAG/tos-neo-companion.git
cd tos-neo-companion
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` builds, runs the offline tests and publishes a self-contained app into `dist\`. Use `-NoTest` to skip the
tests. Then start `dist\Companion.App.exe` as in the quick start. Runtime data lives in `C:\ProgramData\TOS-companion`;
`dist\Companion.Cli.exe` is a small command-line companion (`status`, `dailies`).

**Releases are built by GitHub:** pushing a tag such as `v1.0.1` runs `.github/workflows/release.yml`. It builds
and tests on a Windows machine, then attaches the zip to a new Release.

## Repository layout

| Folder | Contents |
|---|---|
| `src/` | The app: contracts, game I/O (capture, window messages), vision, log reader, perception, engine, dailies, tray UI, CLI |
| `features/` | Feature modules (crafting, inventory, recipes, homestead, survey, Tea House): procedures and screen pictures as data |
| `data/`, `templates/` | Core screen map, procedures, and the small screen pictures the app matches against |
| `tools/` | Template cutter, capture probe, Tea House trainer |
| `tests/` | Offline MSTest suites (no game needed) |
| `docs/` | Manual, daily-checklist design, game-log discoveries, engineering method, fallback protocols, roadmap |

## Tests

```powershell
dotnet test Companion.sln -c Release --filter "TestCategory!=probe&TestCategory!=tune"
```

Tests marked `probe` or `tune` are development tools (long self-play runs, scans of a real game log), not checks.

Some image tests used full screenshots of the author's game. They are **not included** for privacy reasons, and those
tests report as *skipped* (inconclusive). All other tests run offline.

## Maintaining it

- **A screen picture stopped matching after a game update:** capture the screen again and re-cut the picture with
  `tools/Companion.TemplateCutter`. The pictures are listed in `templates/templates.json` and
  `features/*/data/templates.json`.
- **A daily stopped ticking:** the daily rules are data in `data/dailies.json`. [docs/DAILIES_MODULE.md](docs/DAILIES_MODULE.md)
  and [docs/DISCOVERIES.md](docs/DISCOVERIES.md) explain which log lines mean what.
- How changes were made and checked: [docs/ENGINEERING.md](docs/ENGINEERING.md) and [docs/FALLBACKS.md](docs/FALLBACKS.md).

## License

[MIT](LICENSE)
