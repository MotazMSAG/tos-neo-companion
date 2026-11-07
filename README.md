# TOS NEO Companion

A Windows tray helper for **Tree of Savior: NEO** (PC client). It was built for one player's own use and is shared here
in case it helps someone else.

> **No longer maintained.** The author has stopped playing. Game updates will change the screens it reads, so parts
> may stop working until someone re-cuts the screen pictures (see [Maintaining it](#maintaining-it)). Issues and
> pull requests are welcome, but nobody is actively working on it.

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

## Requirements

- Windows 10 or 11 (64-bit), with administrator rights (the app asks for them once per start).
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build it.
- The game in **windowed mode, maximized on a 1920×1080 monitor** (game client area 1920×1009). The screen pictures
  were cut at this size from the **English** client.
- The game folder at `D:\Games\TOS`, **or** an environment variable `TOS_GAME_DIR` pointing at your game folder (the
  one that holds `TOS.exe` and the `Log` folder). For example, in PowerShell:

  ```powershell
  [Environment]::SetEnvironmentVariable('TOS_GAME_DIR', 'C:\Games\TOS', 'User')
  ```

## Build and run

```powershell
git clone https://github.com/MotazMSAG/tos-neo-companion.git
cd tos-neo-companion
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` builds, runs the offline tests and publishes a self-contained app into `dist\`. Use `-NoTest` to skip the
tests.

1. Start `dist\Companion.App.exe` and accept the administrator prompt. A tray icon appears near the clock.
2. The first time, open the tray menu and choose **Trust this build...**. Nothing runs before that. The app then
   refuses to run if its files change later. Its runtime data lives in `C:\ProgramData\TOS-companion`.
3. Left-click the tray icon to open the window. It opens on the **Today** checklist.
4. **Stop now:** tray menu or **Ctrl+Alt+Pause** anywhere.

Crafting automation stays off until you switch it on in **Settings**. `dist\Companion.Cli.exe` is a small command-line
companion (`Companion.Cli status`, `Companion.Cli dailies`).

To uninstall: exit from the tray, delete the folder, and delete `C:\ProgramData\TOS-companion` (as administrator).

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
