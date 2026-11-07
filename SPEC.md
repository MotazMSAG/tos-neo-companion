# TOS Companion: final implementation spec (v1, 2026-10-14)

Contracts are compiled and frozen: `src\Companion.Contracts` (0 warnings, 0 errors) and `tests\Companion.Contracts.Tests` (8/8 pass).
Six builders implement their units in parallel against the contracts alone. One assembler (unit `app-host`) then wires everything together.
`proofs\` stays PowerShell, as evidence, and is read-only. The companion ports the PROVEN logic to C#: focus messages, keys, slide-click guards and timings.
Everything runs out of process; the in-process plugin route was dropped on 2026-10-14.

**Goal.** Run in-game procedures (background keys and window-slide clicks). Verify every step against the game log and a screen capture. When something goes wrong, work out where we are, then step back, reset or retry. The user's only job is one UAC approval per session; the companion escalates to the user only when it is truly stuck.

**Extension goal.** A modular companion that knows the character's stock and the crafting recipes, and automates crafting end to end (plan, execute with recovery, verify, update stock). Live crafting and homestead automation sit behind user on/off switches.

---

## 1. Hard rules as code invariants

| # | Rule | Invariant in code | Enforced in | Test |
|---|---|---|---|---|
| R1 | Never move the real cursor | The cursor and input-injection APIs never appear in source. GetCursorPos, GetCursorInfo, GetClipCursor and GetAsyncKeyState (mouse buttons only) are read-only. A ClipCursor change while we hold fake focus means HALT (F27). | static scan; GameIO watchdog | StaticRulesTests; watchdog tests |
| R2 | Never activate or raise the game | No activation APIs at all. `SetWindowPos` is private to GameIO and called only with hWndInsertAfter=0 and flags in `HardRules.AllowedSetWindowPosFlags` {0x0215, 0x4215}. The emergency ladder alone may also use {0x0214, 0x4214}. Fake unfocus is never sent while the game PID is really foreground; in foreground mode (7.3b) no focus message is sent at all, and the choke point refuses every one for the whole input. | GameIO choke point (runtime assert) | wrapper tests with fake Win32; ForegroundModeTests |
| R3 | Window may only slide | Slide only while a valid cover window (7.5) is under a still cursor, or (foreground mode, 7.3b, the user's decision 2026-11-02) while the game itself is the foreground window under a still cursor and the user has not touched keyboard or mouse. The slid rect must keep a 96-DPI monitor (7.5); its exposed area is journaled, not refused (2026-10-14). The window comes back home in every path, including the watchdog, dead-man, guardian process and crash marker. Any deviation is an incident; a failed restore ladder is a HALT. | GameIO | slide/abort-order tests |
| R4 | No action while the game is minimized, or foreground outside foreground mode | The readiness gate runs before any message: foreground is checked by PID, plus IsIconic, plus a 5 s cooldown after the game was last foreground and 5 s after a foreign RecoverFocus (the user, 2026-10-20; were 60 s and 30 s). Foreground mode (7.3b) needs the game's own window in front for 5 s without a break, no keyboard or mouse input for 2 s (keys 3 s), no mouse button down and the cursor still for 2 s. The watchdog samples the same signals while an input is in progress. | GameIO readiness + engine gates | readiness tests, engine scenarios, ForegroundModeTests |
| R5 | Out of process only | Only window messages from `HardRules.AllowedMessages` (WM_SIZE only on the emergency path), PrintWindow, reading the log, and whitelisted registry reads. OpenProcess only with 0x1000. No memory access, no hooks, no network, no runtime code loading. | static scan + runtime asserts | StaticRulesTests |
| R6 | No emulator or VM | Tokens BlueStacks, HD-Player, adb.exe and Hyper-V are banned. | static scan | StaticRulesTests |
| R7 | Human pace | A token bucket at the GameIO choke point: at least 1 s between input batches, at most 20 per minute, 20 slides per minute, 1200 per hour and 20 focus pairs per minute (raised on purpose 2026-10-14, the user's pace: about one click every 3 s; the tests assert the `HardRules` constants). A key, a slide click and a slide drag are one batch each; a click and a drag are one slide each. Per-procedure budgets apply on top (6.7). | GameIO + engine | pacing tests (fake clock) |
| R8 | Privacy | Journals, diagnostics and fixtures hold codes only. Log text passes `ILogPrivacyFilter`. Images are written only through `IVision.EncodePng` with blackouts. | Logs, Engine | privacy tests |
| R9 | Forbidden UIs | The `UiCatalog` lists are hard-coded. Nothing is clicked while a Forbidden UI is open. If one opens after our input: one Esc, then HALT. A foreign one means pause without Esc. | Engine | scenarios |
| R10 | Automation switches | Commit actions are refused unless `automation.crafting` or `automation.homestead` is on. config.json changes only through the elevated tray. | Engine | scenarios |

**Static check (`tests\Companion.GameIO.Tests\StaticRulesTests`, data in `Companion.Contracts.Safety.StaticRules`)**, run over every `*.cs` under `src\`, `features\` and `tools\`:
1. `ForbiddenTokens`: ordinal, case-sensitive search across code, strings and comments. Skips `src\Companion.Contracts` and `tests\`.
2. `RestrictedTokens`: each token is allowed only in the listed project. Examples: `SetWindowPos`, `PostMessage`, `PrintWindow` and `Registry.CurrentUser` only in Companion.GameIO; `Process.Start` and `RegisterHotKey` only in Companion.App; `Windows.Media.Ocr` only in Companion.Perception.
3. `PInvokeAllowlist`: covers every `DllImport` and `LibraryImport` in every project, Contracts included. The key is `dll!EntryPoint`, with EntryPoint taken from the attribute or else the method name. Projects not listed may declare none.
4. Feature projects (`features\**`) must also pass `FeatureForbiddenTokens`, and their csproj may only `ProjectReference` Companion.Contracts.
5. Runtime asserts (GameIO wrappers):
   - hwnd equals the validated game window;
   - the message is in the allowlist;
   - mouse lParam lies inside the client;
   - SetWindowPos flags are in the allowed set;
   - OpenProcess access is 0x1000;
   - GetAsyncKeyState is called only with keys {1, 2, 4};
   - keys are only the exact (VK, scan) pairs in `HardRules.AllowedKeys` {Esc (0x1B,0x01), B (0x42,0x30)}, and `DeniedKeys` Enter, Y and I are refused even if the whitelist is edited.

## 2. Unit split

All projects target `net10.0-windows10.0.19041.0` with LangVersion latest, Nullable enable and x64 (`companion\Directory.Build.props`). SDK settings: `global.json` pins 10.0.402 with rollForward latestPatch; `NuGet.config` lists only nuget.org and maps package sources to `Microsoft.*`, `MSTest.*`, `System.*` and `runtime.*`.

- **Test packages.** Use exactly `Microsoft.NET.Test.Sdk`, `MSTest.TestAdapter` and `MSTest.TestFramework`, with versions set centrally in `Directory.Packages.props`. Do not use the `MSTest` metapackage or MSTest.Sdk: they pull Mono.Cecil, which is not Microsoft-published, and restore fails. `tests\Companion.Contracts.Tests` is the reference layout.
- **Dependencies.** Units reference ONLY Companion.Contracts, plus the BCL, WinForms/System.Drawing (`<UseWindowsForms>true</UseWindowsForms>`) and the WinRT projection that comes with the TFM. Only Companion.App references implementations.
- **Build and test commands (PowerShell).**
  ```
  $env:DOTNET_ROOT='<repo>\..\.dotnet'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  $env:DOTNET_NOLOGO='1'
  $env:DOTNET_MULTILEVEL_LOOKUP='0'
  & '<repo>\..\.dotnet\dotnet.exe' build|test <proj>   (or the installed dotnet)
  ```

| Key | Projects / folders it owns | Implements (contracts) | Consumes (contracts) |
|---|---|---|---|
| **core-io** | `src\Companion.GameIO`, `tests\Companion.GameIO.Tests` (includes StaticRulesTests) | `IGameIo`, `IStopSwitch` | IClock, ILogTracker, IUiClassifier, IVision, IJournal, CompanionPaths |
| **logs** | `src\Companion.Logs`, `tests\Companion.Logs.Tests` (byte-exact fixtures) | `ILogTracker`, `IUiClassifier`, `ILogPrivacyFilter` | IClock, ICharacterKeyHasher, CompanionPaths |
| **vision-ocr** | `src\Companion.Vision`, `src\Companion.Perception`, `tools\Companion.TemplateCutter`, `templates\` (PNG + templates.json), `data\readers.json`, `tests\Companion.Vision.Tests`, `tests\Companion.Perception.Tests` | `IVision`, `ITemplateCatalogBuilder`, `IOcr`, `IDigitReader`, `IScreenReader`, `IReaderCatalogBuilder` | (none besides models) |
| **engine-nav** | `src\Companion.Engine`, `src\Companion.Navigation`, `data\ui-map.json`, `data\procedures.json`, `tests\Companion.Engine.Tests`, `tests\Companion.Navigation.Tests` | `IProcedureRunner`, `IProcedureCatalogBuilder`, `IScreenService`, `IPauseControl`, `IEngineStatusSource`, `IJournal`, `IArtifactStore` (Engine); `IUiMapCatalogBuilder`, `IScreenIdentifier`, `IPathPlanner`, `INavigator` (Navigation) | IGameIo, ILogTracker, IUiClassifier, ILogPrivacyFilter, IVision, ITemplateCatalog, IReaderCatalog, IScreenReader, IKnowledgeStore, IHaltLatch, INotifier, IAutomationSwitches, IConfigProvider, IClock, IStopSwitch |
| **knowledge-planner** | `src\Companion.Knowledge`, `tests\Companion.Knowledge.Tests` | `IKnowledgeStore`, `ICharacterKnowledge`, `ICharacterKeyHasher`, `IPlanner` | IClock, CompanionPaths |
| **features** | `src\Companion.Features` (loader, registry, built-in `core` feature); `features\Companion.Feature.Inventory`, `.Recipes`, `.Crafting`, `.Survey`, `.Homestead` (each with `data\`); `tests\Companion.Features.Tests` | `IFeatureHost`, `IJobCatalog`, `IFeature` (x6) | IFileTrust, the catalog builders, and IJobContext members |
| **app-host** (assembler) | `src\Companion.App` (elevated WinForms tray, `app.manifest` requireAdministrator + PerMonitorV2, composition root, guardian mode), `src\Companion.Cli`, `Companion.sln`, `companion\build.ps1`, `tests\Companion.App.Tests` | `IHaltLatch`, `INotifier`, `IConfigProvider`, `IAutomationSwitches`, `IFileTrust`, `IClock` (system), pipe server, `IJobContext` | everything |

Data-file owners:
- `templates\` and `data\readers.json`: vision-ocr.
- `data\ui-map.json` and `data\procedures.json`: engine-nav.
- `features\*\data\`: features.
- App includes the core data folders as content: `data\` to `<out>\data\` and `templates\` to `<out>\templates\`.
- Each feature project copies its `data\**` to `<out>\features\<id>\` (Content, `LinkBase`, PreserveNewest).

## 3. Contracts (Companion.Contracts, frozen)

| Namespace | Types (what they are for) |
|---|---|
| `Companion.Contracts` | `PointI`, `PointD`, `SizeI`, `RectI` (Win32 RECT semantics); `IClock` (UtcNow, Monotonic, Sleep; all timeouts use Monotonic); `AutomationSwitch` {None, Crafting, Homestead}; `ContractsInfo.ApiVersion`=1; `CompanionPaths` (install, data, templates and feature data; RuntimeDir = %ProgramData%\TOS-companion; marker, journal, diagnostics, calibration, survey and knowledge paths); `DataFileNames`; `ContractJson.Options` (THE serializer: camelCase, string enums, polymorphic step actions by `"type"`, comments allowed, frozen) |
| `.Safety` | `HardRules` (messages, focus sequences, SWP flags, keys and lParams, proven timings, slide safety, vision floors, pacing, ceilings, engine timing); `StaticRules` (scan data, P/Invoke allowlist, registry whitelist); `UiCatalog` + `UiKind` (Forbidden, Social, Busy, Station, Stack seed, Overlay seed, EscSafe, world-press and combat indicators); `GameKey`, `KeyCode`, `FocusMessage`; `IStopSwitch`, `StopRequest`, `StopSource` |
| `.GameIo` | `IGameIo`; `GameIdentity`, `GameBuildFingerprint` (file size + mtime only), `MonitorInfo`, `GameWindowPrefs`, `GameWindowState`, `ConnectResult`/`ConnectProblem`, `ReadinessQuery`/`ReadinessReport`/`NotReadyReason`, `CoverInfo`/`CoverRejectReason`, `SlidePlan`, `KeyRequest`/`KeyReport`, `ClickRequest`/`ClickReport`, `DragRequest`/`DragReport` (2026-10-14), `InputDisposition`, `AbortReason`, `InputPhase` (P0-P7), `InputMarks`, `CursorEvidence`, `SlideEvidence`, `WatchdogStats`, `WindowIncident`/`IncidentKind`, `RestoreRung`, `RestoreReport`, `HomeCheck`/`DeviationOwner`, `CaptureResult`/`CaptureProblem`, `ResponsivenessReport`, `PacingStatus` |
| `.Logs` | `LogFileIdentity`, `LogPosition` (offset + generation); events `LogEvent` and its subtypes `UiLogEvent`(UiVerb), `FocusLogEvent`, `PressBlockLogEvent` (`ToClient(h)` = (x, h-1-y)), `ResolutionLogEvent`, `AutoCollectLogEvent`, `SceneLoadLogEvent`, `CraftTickLogEvent`, `JankLogEvent`, `SessionLogEvent`, `CharacterLoginLogEvent` (hashed), `ThrottleLogEvent`, `PayLogEvent`, `AutoEatLogEvent`, `GuideBanLogEvent`; `UiExpectation` (exact verb+name); `ILogTracker`, `UiStateSnapshot`, `CraftSessionInfo`, `BusyKind`, `FocusState`, `LogPollResult`, `LogHealth`, `LogBootstrapReport`; `IUiClassifier`; `ILogPrivacyFilter` |
| `.Vision` | `Frame` (BGRA client capture, in memory only), `GrayImage`, `FrameQuality`, `HudState`, `HudClassification`, `TemplateDefinition`, `TemplateStatus`, `TargetTags`, `FireOn`, `MatchResult`/`MatchFailure`; `ITemplateCatalog`(+`Builder`), `IVision` |
| `.Perception` | `IOcr` (+`OcrOptions`, `OcrResult`, `OcrLine`, `OcrWord`, `OcrStatus`, `Binarization`), `IDigitReader`/`DigitReadResult`, `ReaderDefinition`, `RegionDefinition`, `ScreenIdentity`, `ParseRule`, `PairSemantics`, `ReaderStatus`, `ReadValue`/`ReadStatus`, `ScreenReadResult`, `IReaderCatalog`(+`Builder`), `IScreenReader` |
| `.Engine` | Steps as data: `StepDefinition`, `StatePredicate`, `Expectation`, `StepFlags`, `StepAction` and its subtypes (`KeyAction`, `ClickAction`, `DragAction` (2026-10-14), `ToggleHudAction`, `RestoreHudAction`, `ResetAction`, `WaitAction`, `NavigateAction`, `CaptureAction`/`CaptureScope`, `ReadAction`, `CallProcedureAction`, `AwaitCraftAction`, `NeedsSurveyAction`), `ProcedureDefinition`, `ProcedureLimits`, `ProcedureKind`. Outcomes: `StepOutcome`, `OutcomeClass`, `RecoveryAction`, `OutcomePolicy.ClassOf`, `HaltCode`. Runtime: `RunContext`, `JobKind`, `ProcedureResult`/`ProcedureStatus`, `StepAttemptRecord`, `PendingCompensation`, `HaltReport`, `IProcedureRunner`, `IProcedureCatalog`(+`Builder`), `IScreenService`, `IPauseControl`, `IHaltLatch`, `EngineStatus`/`EngineState`, `IEngineStatusSource`, `IJournal`/`JournalRecord`/`JournalKind`, `IArtifactStore`/`ArtifactCategory` |
| `.Navigation` | `UiMap`, `ScreenDefinition`, `ButtonDefinition`, `ButtonSafety`, `SurveyStatus`, `DenyZone`, `IUiMapCatalog`(+`Builder`), `ScreenIdentification`/`IdentificationConfidence`, `NavigationPath`, `NavigationResult`/`NavigationStatus`, `IScreenIdentifier`, `IPathPlanner`, `INavigator` |
| `.Knowledge` | `CharacterKey` (salted hash; since 4.33 only the knowledge profile's folder name), `ICharacterKeyHasher`, `ItemKey` (normalised on-screen name), `ValueRange`, `StockSource`, `ObservationKind`, `StockEntry`, `StockObservation`, `DerivedStockDelta`, `StaleScope`, `MaterialRequirement`, `Recipe`, `RecipeConfidence`, `KeepStockRule`, `StockView`, `ICharacterKnowledge`, `IKnowledgeStore`, `KnowledgeLoadReport` |
| `.Planning` | `CraftGoal`, `PlannerPolicy`, `RecipeChoicePolicy`, `PlanRequest`, `PlannedCraft`, `Shortfall`/`ShortfallKind`, `PlanIssue`/`PlanIssueKind`, `TimeRange`, `CraftPlan`, `IPlanner` |
| `.Features` | `IFeature`, `FeatureManifest`, `FeatureDependency`, `FeatureDataFiles`, `IFeatureRegistrar`, `JobDefinition`, `JobParamSpec`/`JobParamType`, `JobParameters`, `JobState`, `JobResult`, `IJobHandler`, `IJobContext`, `IJobCatalog`, `IFeatureHost`, `FeatureLoadContext`, `FeatureLoadReport`/`FeatureLoadStatus` |
| `.Host` | `CompanionConfig` (+`AutomationConfig`, `InputConfig`, `PolicyConfig`, `KeepStockConfig`), `IConfigProvider`, `IAutomationSwitches`, `INotifier`/`Notification`/`NotificationKind`, `IFileTrust`, `PipeProtocol`, `PipeOp`, `PipeRequest`, `PipeResponse`, `HostStatus`/`HostState`, `JobStatusInfo` |

Contract rules:
- Records are immutable.
- Interfaces document their threading.
- Every implementation's public surface is exactly the contract interface plus a constructor that takes contracts, primitives or `CompanionPaths`.
- A builder who needs a contract change stops and reports. They never fork types.

## 4. Construction and composition (side-effect free)

- **Constructors only store dependencies.** No window lookups, threads, files, timers or registry access. Work starts only in explicit methods:
  - `IGameIo.Connect`;
  - `ILogTracker.Bootstrap` and `Poll`;
  - `IKnowledgeStore.Load`;
  - catalog `Add` and `Seal`;
  - `IFeatureHost.LoadAll`;
  - the App's `Start`.
- **Nothing touches the game until a job runs.** The one exception is crash recovery at host start: if a slide marker proves we left the window displaced, it is restored, move-only (7.6).
- **App start sequence** (`Program.Main`, elevated):
  1. `--guardian <hostPid> <hostStartTicks>` mode? Then run the guardian (10.1) and exit.
  2. Assert 64-bit and elevated. Take the single-instance mutex `Local\TOS-companion.host`; if it is held, notify "already running" and exit. Treat AbandonedMutexException as acquired.
  2a. Resolve the game folder once, before the host is created (no environment variable needed): `TOS_GAME_DIR` when it names a folder with TOS.exe; else the folder saved in `RuntimeDir\game-folder.json` when it still holds TOS.exe; else the running game's folder (`GameFolderLocator`: the same read-only search as `Connect` without the exe comparison, OpenProcess 0x1000 only; TOS.exe with GameAssembly.dll next to it); else the usual places (`D:\Games\TOS`, then on every fixed drive `\Games\TOS`, `\TOS`, `\Program Files\TOS`, `\Program Files (x86)\TOS`; existence checks only, TOS.exe and GameAssembly.dll); else a small dialog before the tray (Try again / Choose folder... / Exit). A found or chosen folder is saved (atomic SEC-2 write). The guardian uses the variable, then the saved folder, then `D:\Games\TOS`, and never shows a dialog. The tray's Advanced > Game folder... saves another folder for the next start. The exe-path identity check of `Connect` is unchanged: a game started from another folder is refused (ExePathMismatch) and the tray says so once, in plain words.
  3. Create or verify RuntimeDir: ACL Administrators + SYSTEM full and the interactive user read-only; refuse reparse points.
  4. Load config.json (missing means `CompanionConfig.Default`) and the HALT latch.
  5. Load the trust list. Hash every install-side file (code: `*.dll`/`*.exe` in InstallDir; data: `data\`, `templates\`, `features\`). On mismatch, HostState = Untrusted and no jobs are accepted until the user clicks "Trust this build…" in the tray (10.1).
  6. Construct (no side effects): SystemClock, StopSwitch, Journal, CharacterKeyHasher, KnowledgeStore, UiClassifier, PrivacyFilter, LogTracker, TemplateCatalog, Vision, Ocr, DigitReader, ScreenReader, ReaderCatalog, UiMapCatalog, ProcedureCatalog, GameIo, Planner, ScreenIdentifier, PathPlanner, FeatureHost, ProcedureRunner, ScreenService, Navigator.
  7. Load data once. Read every core data file with `IFileTrust.ReadTrusted`, deserialize with `ContractJson.Options` (templates.json = `TemplateDefinition[]`, ui-map.json = `UiMap`, procedures.json = `ProcedureDefinition[]`, readers.json = `ReaderDefinition[]`) and pass to the builders. Then `FeatureHost.LoadAll(features compiled into the build, context)`. Then `Seal()` all catalogs and validate cross-references (templates, readers and screens referenced by procedures and the map). A broken feature is disabled; a broken core file sends the host to HostState.Untrusted with its reason.
  8. Start the log tracker (`Bootstrap`, then a poll timer every 500 ms; this reads only) and `KnowledgeStore.Load`. Spawn the guardian. Run `GameIo.Connect` only if the game is running; otherwise a job connects at its own start (incident 28). The status's "game connected" is `IGameIo.GameWindowPresent` (4.33, incident 39): the held window still lives, or exactly one game window of the right exe is there (read-only: nothing is sent, nothing is bound), so a game that was closed and opened again no longer reads "not running" until the next job. Run `RecoverLeftoverDisplacement`.
  9. Start the pipe server, tray, heartbeat timer (1 s, independent of the engine thread) and the engine thread.
- **Never reload.** No code or data file is read again after step 7. Static test: no `File.*`/`Directory.*` read of install-side paths outside the loader classes. Runtime files (config written by the tray, knowledge, journal, markers) are runtime data, not code.

## 5. DI and testability

- The engine and navigator see only contracts: IGameIo, ILogTracker, IUiClassifier, IVision, ITemplateCatalog, IScreenReader, IClock, IJournal, IStopSwitch, IHaltLatch, INotifier, IAutomationSwitches, IConfigProvider, IKnowledgeStore, IUiMapCatalog, IScreenIdentifier and IPathPlanner. Every recovery path is therefore testable with scripted fakes:
  - FakeGameIo returns a `ClickReport`/`KeyReport` per call;
  - FakeLogTracker appends events and serves `State`;
  - FakeVision returns scripted `MatchResult`/`HudClassification` per capture;
  - FakeClock advances in `Sleep`.
- Fakes record calls in a shared list, so tests assert counts and order (e.g. "exactly one Esc, then HALT").
- **GameIO has an internal seam** (`internal interface IWin32`, not a contract). Its tests drive the click and abort state machines against a fake message sink, a fake window table and fake cursor samples. No real window is touched.
- **The safety gate is not injectable.** Readiness, cover, monitor, home and pacing checks run inside GameIO's `PressKey`/`SlideClick` (PITFALL-18). Engine tests fake the whole IGameIo; GameIO tests fake only Win32.
- **Tests are offline only.**
  - They never call acting functions on real windows: no messages, no SetWindowPos, no PrintWindow of a real window, no elevation.
  - They never start Companion.App.
  - Reading `proofs\out\*.png` and byte-exact log fixtures (allowed lines only) is fine.

## 6. Game state, log model and recovery protocol

### 6.1 Log reading (`ILogTracker`, LOG-11, PITFALL-05/09/13, F14)
**File handling.**
- File: `<game folder>\Log\log.txt` (the game folder of the start sequence, step 2a). Open each poll with `FileShare.ReadWrite | FileShare.Delete` and close immediately, so the game's rename at startup never fails.
- Take the length from the open handle. Read bytes from the offset to EOF, at most 8 MB per poll.

**Entries.**
- An entry is a line matching `^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\t[A-Za-z]+\t`, plus any following `|\t` lines, ended by a blank line or the next timestamped line.
- Decode UTF-8 only up to the last complete entry; the next offset starts right after it. Strip CR. Accept any level (Log, Warning).
- Drop untimestamped lines except `|\t` lines directly under `UpdateModule LostFocuse` (and keep only its `module:` and `screenPosition:` fields).

**Rotation.** The identity is (CreationTime, file index via `GetFileInformationByHandle`, SHA-256 of the first line). Check it every poll. If it changed, or if the length is below the offset:
1. Set `Rotated`.
2. Reset the model to Unknown and set `GameAvailable=false`.
3. Re-run the bootstrap from byte 0 of the new file.

**Bootstrap.** Forward byte search for ASCII needles: `set_auto_collect_status`, `req_login_role`, `[graphic] Current screen resolution`, `[scene_load]`, `open const ui`, `hide top ui`, `OnApplicationFocus`. Decode only the matching lines. Fully parse the last 2 MB to rebuild the advisory stack. The target is under 2 s for 80 MB.

**Events kept.** Ring buffer of the last 15 min or 50 000 events.

**Message patterns.** Each pattern is anchored on the message part, after `ts\tLevel\t`. Builders must verify each one against byte-exact fixtures from the real log, using only allowed lines. In the real log the separators are 4 spaces; parse them as `\s+`.

| Event | Pattern |
|---|---|
| UiLogEvent | `^\[LOG\] -> module/foundation/ui/ui_mgr\.lua:\d+\s+(<color=yellow>show_ui</color>\|open const ui\|\[ui mgr\]hide top ui\|\[ui_mgr\]add to close_list\|stack over -> remove\|\[ui_mgr\]dispose_ui immediately\|\[ui_mgr\] dispose_all_close\|\[ui_mgr\]check_clean_unused - do dispose\|check can open fail\d*\|【to_hide】 hide_ui)\s+([A-Z][A-Z0-9_]*\|nil\|\d+)\s*$` |
| PopupRealShow | `^\[LOG\] -> \S*popup_seq_mgr\.lua:\d+\s+.*\[real show ui\]\s*tar_data\.ui_id\s+([A-Z][A-Z0-9_]*)\s*$` |
| GuideBan | `^\[LOG\] -> module/foundation/ui/ui_mgr\.lua:\d+\s+.*\bban by guide( mask)?\b` |
| Focus | `^\[StageMgr\] OnApplicationFocus (RecoverFocus\|LostFocus)\s*$` (no `[LOG] ->` prefix) |
| PressBlock | `^UpdateModule LostFocuse(?: Name: (.*) \(UnityEngine\.GameObject\))?\s*$` and continuation lines `^\|\tmodule: Name: (.*) \(UnityEngine\.GameObject\)` and `^\|\tscreenPosition: \((-?[\d.]+), (-?[\d.]+)\)` |
| Resolution | `^\[LOG\] -> \S+\.lua:\d+\s+\[graphic\] (Current screen resolution\|Device resolution)\s+(\d+)\s+(\d+)` |
| ScreenSizeChange | `Unity Application Notify: OnScreenSizeChange` (line start, with or without the `[LOG] ->` prefix) |
| AutoCollect | `^\[LOG\] -> \S+\.lua:\d+\s+\[collect_mgr\] set_auto_collect_status\s+(\d+)` (a traceback follows on the same line) |
| SceneLoad | `^\[LOG\] -> cave/cave_(cli\|helper)\.lua:\d+\s+\[scene_load\]\s*(load_scene start load\|before loading_empty\|load_scene end load\|time sample load scene need wait\|on_cave_role_ready\|hide_progress_now\|load_scene hide_progress real\|abort hiding progress)` |
| CraftTick | `^\[LOG\] -> module/lifeskill/cook/lifeskill_cook_mgr\.lua:\d+\s+is_block\s+(-?[\d.]+)\s+(-?[\d.]+)` |
| Jank | `^(\[LOG\] -> \S+:\d+\s+)?\[jank_detector\] jank over\s*:\s*([\d.]+)` |
| Session | `--->>> add to unity log callback`; `^\[StageMgr\] OnApplicationQuit`; `\[emasdk\]start logout`; `\[ema\] dc_ping log trigger` (each recognised, never stored) |
| CharacterLogin | `select_role_view\.lua:\d+\s+\[login\] req_login_role\s+(\S+)`: hash at once with `ICharacterKeyHasher`; the raw token is discarded |
| Throttle | `\[base_network\.lua\] request too frequency` |
| Pay | `^\[LOG\] -> module/shop/pay_mgr\.lua:\d+\s+(.*)$`, except `[pay mgr]on_cave_role_ready` and `req get pay show` |
| AutoEat | `\[lifeskill\]\[autoeat\]drug_mgr supply\.eat_food req` (item id dropped) |

### 6.2 UI model (`UiStateSnapshot`, PITFALL-06, CR-03)

**Classification** (`IUiClassifier`): Forbidden > Social > Busy > Station > Stack (seed or learned from `hide top ui`) > Overlay (seed) > Unknown. A learned Stack overrides the Overlay seed. Names need no `UI_` prefix.

**Advisory stack** (advisory only, never the sole basis of a decision):
- Show or OpenConst of a Stack, Forbidden, Social or Unknown UI pushes it. This is idempotent: a name already present moves to the top.
- HideTop X, AddToCloseList X, StackOverRemove X, DisposeAllClose X, CheckCleanDispose X and ToHide X remove X.
- DisposeImmediately, numeric ids and `nil` close ids are ignored.
- `hide top ui nil` clears the stack and records `LastHideTopNil`.
- Scene `StartLoad` clears the stack.
- A rotation or session marker resets everything to Unknown.

**HudClean** (engine-side) requires all of:
- a `hide top ui nil` whose offset is after one of OUR Esc KEYDOWN marks;
- no Stack, Forbidden, Social or Unknown show or open-const since then;
- no scene load since then;
- no rotation since then;
- no game-foreground interval since then.

**Busy:**

| BusyKind | Starts | Ends |
|---|---|---|
| Loading | `StartLoad` or a show of UI_LOAD_PROGRESS | `HideProgressReal`; the engine then also needs 2 stable HUD captures |
| Cinema, Scenario | UI_CINEMA_* or UI_SCENARIO* shown | any close verb, or 120 s without a related event (then the HUD signature is checked) |
| Login | logout, UI_SERVER or UI_SELECT_ROLE | `open const ui UI_MAIN` |
| Revive | UI_REVIVE | closed, or 60 s |
| GuideMask | a `ban by guide mask` within the last 2 s, or UI_GUIDE_MASK open | condition no longer holds |
| Match | UI_TEAM_3V3_* | open, or shown within 120 s |

**Other state:**
- **GameAvailable** becomes false at a rotation or session marker, and true again after `open const ui UI_MAIN` plus `HideProgressReal`.
- **Craft session:**
  - It starts at the first `is_block` or at a Station show, and ends 2 s after any close verb of the station UI.
  - `Ticks` = is_block count − 1.
  - `CraftViewReshown` is set when a UI_LIFESKILL_MAKE_VIEW show follows within 1 s of the end.

### 6.3 State vector and global gates (checked in this order before EVERY input batch)

| # | Gate | If false → outcome |
|---|---|---|
| 1 | Stop switch / pause / HALT latch | Stopped / (paused wait) / Refused |
| 2 | Build trusted, catalogs sealed | TamperedFiles (HALT) |
| 3 | Game identity (IsWindow, PID, StartTime, class) unchanged since job start | IdentityChanged (HALT) or GameExited if process gone |
| 4 | Log generation unchanged since job start; log grew within 5 s | GameExited / LogQuiet (Wait) → LogStalled at 30 s (HALT) |
| 5 | GameAvailable, Busy == None | Busy (Wait) |
| 6 | Display mode: windowed (prefs mode 3), maximized, client 1920x1009, caption, home monitor present, DPI 96; no extra visible game window | DisplayModeWrong (Wait, notify once) / ExtraGameWindow (HALT) |
| 7 | Not foreground (PID), not iconic, cooldowns (60 s since foreground, 30 s since a foreign RecoverFocus/OnScreenSizeChange) | UserActive (Wait) |
| 8 | Session interactive (GetCursorPos ok, input desktop, not locked); no clock jump | SessionUnavailable / ClockJump (Wait) |
| 9 | Auto-collect status known and treated as idle (0, or a value the user confirmed in the tray) | AutoCollectNotIdle (Wait) |
| 10 | No combat indicator in 10 s (clicks and commits only) | CombatRecent (Wait) |
| 11 | No `request too frequency` in 60 s; pacing token available | Throttled / RateLimited (Wait) |
| 12 | Foreign Forbidden/Social UI not open; no craft in progress (unless the step is AwaitCraft) | ForeignBlockingUi / CraftInProgress (Wait) |
| 13 | Commit step: switch on, no user activity in the game since the job started (CR-07); any click step: `input.slideClick` on | Refused |
| 14 | Step `Pre` | PreconditionLost / PreconditionTransient |

**Transient vs non-transient precondition loss.**
- *Transient* (wait under patience): Busy, a popup shown in the last 2 s, HUD `Unknown` within 3 s of an Esc or toggle (animation), user activity.
- *Non-transient* (counted): a template missing in 2 stable captures, HUD `Unknown`/`Absent` outside loading, or Pre still false after its transient cause has cleared.

### 6.4 Executing a step

**Pre-flight (whole procedure, before any input).**
- Every ClickAction/Toggle template must exist and be clickable (Verified, SourceScale 1, not suspect).
- Every reader must be Ready, and no NeedsSurveyAction may be reachable — unless the step is `Optional`: an Optional step whose target is not clickable is skipped at run time and recorded as PendingCompensation `skipped.<stepId>` (one notification per run at most; `RestoreHudAction` notifies only if the final HUD state differs from the initial one).
- Any violation ends the procedure as NeedsSurvey with ZERO inputs (detail `template.provisional:<id>`, `template.suspect:<id>` or `reader.needsSurvey:<id>`).
- Slide clicks with `input.slideClick=false` lead to Refused.

**Per action:**

| Action | Execution |
|---|---|
| `KeyAction` | Letter keys require Pre.HudClean. Calls `IGameIo.PressKey`. |
| `ClickAction` | **1. Locate.** Fresh capture (`IScreenService.Capture`). HUD targets need HudClean, the HUD signature and HUD state ≠ Covered/Absent in the same capture. Run `IVision.Locate` on 2 captures ≥ 300 ms apart that agree within 2 px. A match outside the radius is LayoutChanged (HALT). Not found is PreconditionLost. **2. Deny zones.** If the click point lies inside a deny zone inflated by 8 px: LayoutChanged (HALT). If a deny zone is within 120 px: `NearDenyZone=true`. **3. Wait for readiness.** Poll `CheckReadiness` passively every 100 ms. Once ready, re-capture and re-locate if the locate is older than 1.5 s. **4. Click.** `IGameIo.SlideClick`. **Offset** (`ClickAction.Offset`, live run 2026-10-14): the click lands at the found centre + offset (a grid cell placed from a located anchor); deny zones and readiness use that point, GameIO verifies the template at its own centre while slid or, for an opted-in anchor + offset press whose anchor slid off the drawn monitor, the press patch (7.3 off-screen anchor patch rule). Never on a commit step or commit template; outside the client is Refused. |
| `DragAction` | (2026-10-14, 7.3a) `{templateId, offset, delta}`. Locate the anchor twice as a click; press point = found centre + offset, release point = press + delta; both inside the client (else Refused), neither in a deny zone inflated by 8 px (LayoutChanged); readiness polled for BOTH points (no aim shifts). Then `IGameIo.SlideDrag`: one input batch and one click of the budget; while slid GameIO verifies the anchor at its own centre or, on the engine's opt-in when the anchor slid off the drawn monitor, the press patch at the press point (7.3 off-screen anchor patch rule). The step expects nothing (validation refuses UI/HUD/screen expectations, a Commit flag, a zero delta or one over 400 px); a delivered drag is Ok ("drag.done", or "drag.partial" when it was cut short) and is never retried by the engine: the caller measures the effect on a capture. A UI opened by the release is classified like a click's (forbidden, unknown...). Never on a commit template (pre-flight Refused). |
| `ToggleHudAction` | `ClassifyHudStable`. Unknown → PreconditionTransient. Covered/Absent → PreconditionLost. Already at the target → Ok with no click (journal `toggle.already`). Otherwise click the toggle template OF THE CURRENT STATE (`hud.toggle.collapsed` to expand, `hud.toggle.expanded` to collapse) through the ClickAction path. The expectation is the HUD state only: no show_ui is known for the toggle. |
| `RestoreHudAction` | Toggle to the HUD state recorded at procedure start (classified before Reset when the HUD signature is visible, otherwise right after Reset). If the needed template is not clickable: PendingCompensation `hud.restore.<state>` plus one notification. |
| `ResetAction` | See 6.5 (Reset). |
| `WaitAction` | `IClock.Sleep`; no input. |
| `NavigateAction` | `IScreenIdentifier` + `IPathPlanner`. Steps are inlined into this run; their budgets count. |
| `CaptureAction` | Capture, then persist per `CaptureScope` via `IVision.EncodePng` and `IArtifactStore`. |
| `ReadAction` | `IScreenService.Read`. Status ≠ Ok gives a Failed job result, not a HALT. Journaled as a Decision `read` with one code per region: `r.<region>` = `<status>:<detail>:c<confidence %>` plus `:g<glyph score x1000>` when a digit template ran (2026-10-15, e.g. `Unknown:digit-low-score:c0:g741`); never the read text. |
| `CallProcedureAction` | Recursive run with a shared budget; max depth 3. |
| `AwaitCraftAction` | No input. Wait for the craft session to end. Timeout = ticks × max learned cadence (default 3.5 s) + 15 s. |
| `NeedsSurveyAction` | Unreachable after pre-flight. |

### 6.5 Classification

**Attribution.** A UI event is ours if its offset is after `Marks.ActionPosted` and its timestamp falls within 1.5 s after DOWN or KEYDOWN. It is foreign if none of our input batches happened in the 2 s before it.

**Settle.** Before deciding NONE, read until the step timeout (≥ 3 s; ≥ 6 s for a UI not yet shown this session), then 1.5 s more.

**Click** (after `ClickReport`):
1. Disposition ≠ Sent maps directly:
   - NotSent → NotSent; RateLimited → RateLimited; Stopped → Stopped; GameGone → GameExited.
   - IntegrityMismatch → IntegrityMismatch (HALT); Refused → Refused.
   - FocusFailed → FocusFailedOnce, and FocusFailed (HALT) on the second one in the job.
   - UserTookOver → UserActive.
   - AbortedAfterFocus → Popup when the abort was `LogGatePopup` or `SlidTemplateMismatch`, else AbortedAfterFocus.
   - NotReady reasons: GameForeground/Iconic/Cooldown → UserActive; SessionNotInteractive → SessionUnavailable; ExtraGameWindow → ExtraGameWindow; DisplayModeWrong → DisplayModeWrong; everything else → NotSent.
   - Incident → GameSelfResize, RestoreFailed or CursorClipChanged (each a HALT), or WindowIncidentRestored.
2. Sent. Let E be the attributed events from `Marks.FocusSent` to the end of the settle. Check in this order:
   - A Pay line or Forbidden UI in E → **ForbiddenUi**.
   - GuideBan → **GuideBan**.
   - An expected UiExpectation in E → **Ok** (or **LateSuccess** if found only in the settle read; never retried).
   - UI_LOCK_TARGET, UI_LOCK_ARROW or UI_NPC_TALK within 1 s of DOWN → **WorldPress**.
   - `aimVerified` = no cursor movement in P1-P4, AND PressBlock present, AND `PressBlock.ToClient(1009)` within 2 px of the click point, AND unfocus sent while slid, AND no forced slide-back.
   - Another Stack UI → **WrongUiVerifiedAim** if aimVerified, else **WrongUiUnverified**.
   - A Social or Busy UI → **ForeignBlockingUi** / **Busy**. An Unknown UI → **UnknownUi**.
   - No LostFocuse block in [FocusSent, End] → **PressNotRegistered**.
   - aimVerified and empty ObjectName → **WorldPress** (nothing under the pointer at the target).
   - A jank line in [FocusSent, End], or Cursor.MovedInP4, or a forced slide-back → **Uncertain**: settle 2 s, re-run this list, and if still nothing → RetryUncounted.
   - Otherwise → **None** (counted).

**Key:**
- `!FocusLogged` → FocusFailedOnce, and FocusFailed on the second.
- Expected UI → Ok/LateSuccess.
- Forbidden → ForbiddenUi.
- Another Stack UI → WrongUiVerifiedAim. Keys are deterministic, so the second one is RecalibrateTemplate (subject = key).
- Nothing → None.

**Reset** (Esc, F20). Loop with at least 1 s between Escs, at most min(10, `MaxEsc`):
1. HudClean → Ok.
2. Advisory top T is Forbidden or Social, and foreign → ForeignBlockingUi (no Esc).
3. T is not Esc-safe, was not opened by this job, and `policy.closeUserUisAtJobStart` is false → UserUiOpen (park + notify once).
4. Otherwise press Esc and expect `HideTop(T)` or `HideTop(nil)`:
   - any non-overlay show or open-const after our Esc → **EscOpenedUi** (HALT, no further Esc);
   - `nil` → HudClean;
   - `HideTop(X)` → progress, continue;
   - no event → a no-progress Esc.
5. Two no-progress Escs → PreconditionLost (non-transient).

The HUD state is journaled before and after every Reset (F07: whether Esc collapses the expanded menu is unproven).

**Toggle** (F07):
- At most one press per verified-opposite state.
- After a press with a LostFocuse block, never press again until 2 stable captures show the state unchanged; that case is **None** (counted).
- No block → PressNotRegistered (retry allowed).
- Target reached on 2 stable captures → Ok.

### 6.6 Outcome → class → action (classes fixed by `OutcomePolicy.ClassOf`)

| Outcome | Class | Action |
|---|---|---|
| Ok, LateSuccess | Ok | Proceed (a late success is never retried) |
| NotSent | Wait | WaitThenRetry. Passive readiness polling, no messages, no back-off needed |
| AbortedAfterFocus | Wait | WaitThenRetry. Back-off 2, 4, 8 … 60 s; at most 6 focus pairs per minute |
| Uncertain, PressNotRegistered | Wait | Settle 2 s, then RetryUncounted. After `MaxUncountedPerStep` (4), the next one is counted as None |
| UserActive | Wait | Wait for the cooldowns, then re-identify the screen and continue from the latest step whose Pre holds. Commit jobs end Cancelled (CR-07) |
| UserInterference | Wait | Close the UI if Esc-safe and ours, then Park. Commit jobs end Cancelled |
| Popup | Wait | Foreign Esc-safe popup: one log-verified Esc, then retry. Forbidden/Social: ForeignBlockingUi |
| ForeignBlockingUi, Busy, CraftInProgress, CombatRecent, AutoCollectNotIdle, DisplayModeWrong, SessionUnavailable, LogQuiet | Wait | No input. Poll every 1 s; notify once where noted |
| FocusFailedOnce | Wait | Retry once after 5 s |
| ClockJump | Wait | Restart step timers; settle the log 2 s |
| RateLimited / Throttled | Wait | Until the token is available / 60 s back-off |
| WindowIncidentRestored | Wait | Pause 30 s, re-check home, continue. A third one within an hour → HALT TooManyWindowIncidents |
| PreconditionTransient | Wait | Wait under patience |
| WrongUiUnverified | Wait | Esc if the UI is ours and Esc-safe, then RetryUncounted |
| UnknownUi | Wait | One Esc plus a survey capture (TemplateCrops), then retry. A second one in the job → HALT UnknownUiTwice |
| UserUiOpen | Wait | Park and notify once |
| None | Counted | RetryCounted |
| PreconditionLost | Counted (per procedure) | StepBack to the latest earlier step whose Pre holds; if there is none, ResetAndRestart. More than `MaxPreconditionLost` (2) → HALT PreconditionLostExhausted |
| WrongUiVerifiedAim | Counted | Esc (verify `hide top ui X`). ONE retry, only if a fresh full-scale capture finds the template unique within its radius (else the template is suspect now). A second one marks the template suspect for the rest of the session (`ITemplateCatalog.MarkSuspect`) and HALTs RecalibrateTemplate. No restarts |
| All Halt outcomes | Halt | Compensate with keys only (an Esc only for a Forbidden UI we opened), write the diagnostics bundle, latch, notify |
| GameExited, Stopped, Refused, NeedsSurvey, Cancelled | Terminal | End the job; no restore attempts after GameExited |

**Ladder** (counted): 3 counted failures on a step → step back once → full Reset + restart (at most 2 restarts) → HALT CountedFailuresExhausted.

**Waits:** patience 10 min (per procedure `PatienceMs`), then Park.

### 6.7 Limits and pacing
- **Per procedure** (`ProcedureLimits`, data may lower but never raise them past `HardRules.Ceiling*`):
  - 25 input batches;
  - 6 clicks;
  - 8 Escs (Reset alone: 10);
  - 3 counted failures per step;
  - 4 uncounted retries per step;
  - 1 step-back per step;
  - 2 restarts;
  - 2 non-transient precondition losses.
- Hitting any budget means HALT BudgetExceeded.
- **Cycle detector:** reaching the same (step, outcome, UI-state hash) more than 8 times in one job means HALT RepeatedState.
- **Choke-point pacing** (GameIO, R7): ≥ 1 s between batches, ≤ 20 per minute, ≤ 20 slides per minute, ≤ 1200 per hour, ≤ 20 focus pairs per minute (`HardRules`, raised 2026-10-14). A slide drag counts as one batch, one slide and one focus pair (and one click of the procedure budget).
- **Host job limits:** ≤ 10 jobs per hour, ≤ 1 queued + 1 running.

### 6.8 Park, compensation, pause, kill switch
- **Park.** The job keeps its slot and its state is in memory. Compensation is keys only: Esc closes the UIs this job opened (when Esc-safe), and the pending HUD-toggle undo is recorded. The job resumes automatically once the gates clear (idle and a valid cursor spot). After 30 min parked, notify once. After 4 h the job expires as Cancelled (notified).
- **Pause** (tray or pipe). The engine stops at the next gate; an input in progress completes or aborts through its own guards. Resume continues from the gate.
- **Kill switch.** Sources: the Ctrl+Alt+Pause hotkey (App `RegisterHotKey`), the tray "Stop now" item, the pipe `stop` op and SessionEnding. All of them call `IStopSwitch.RequestStop`. GameIO refuses new inputs at once. A slide in progress is ended by the watchdog within one sample (7.4). The job ends Stopped. Inputs stay refused until the user clears the switch in the tray. Target: under 100 ms from request to window at home.
- **HALT latch.** Persisted in `halt.json`; survives restarts until the user clears it in the tray. A new pipe submission never clears it.

### 6.9 HALT messages (fixed text, plain language; Subject = template, UI or step id)

| HaltCode | Message to the user |
|---|---|
| ForbiddenUi | "A shop or purchase screen opened. I closed it once and stopped. Nothing was bought. Check the game." |
| EscOpenedUi | "Pressing Esc opened a screen instead of closing one. I stopped; please look at the game." |
| GuideBan | "A tutorial step is blocking the game's menus. Finish it in the game, then clear the stop in the tray." |
| ExtraGameWindow | "The game opened an extra window (security or login dialog). I stopped." |
| CursorClipChanged | "The game tried to lock your mouse while I was working. I let go and stopped." |
| RestoreFailed | "I could not put the game window back. Double-click the game's title bar to maximize it again." |
| GameSelfResize | "The game changed its own window size during a step. I stopped; maximize the game again." |
| LayoutChanged / RecalibrateTemplate | "The button '<Subject>' is not where I expected; the screen layout changed. Run Calibrate, then update the template." |
| WorldPress | "A click landed in the game world instead of a button. I stopped so nothing else happens." |
| FocusFailed / IntegrityMismatch | "The game does not react to me. Run both the game and the companion as administrator (one UAC prompt)." |
| IdentityChanged | "The game restarted while I was working. Start the job again." |
| LogStalled | "The game log stopped updating. I stopped because I can no longer check what happens." |
| RepeatedState / BudgetExceeded / CountedFailuresExhausted / PreconditionLostExhausted | "I tried '<Subject>' several times and it did not work. I stopped; details are in the diagnostics folder." |
| CraftStoppedEarlyTwice / BlockedCraftSpot | "Crafting stopped early twice (inventory full, out of energy, or a blocked spot). I stopped." |
| AutoCollectChanged | "Auto-gathering changed while I was working. I stopped so I do not interrupt it." |
| UnknownUiTwice | "Screens I do not know opened twice. I stopped; run a Survey so I can learn them." |
| TooManyWindowIncidents | "The game window moved unexpectedly several times. I stopped." |
| TamperedFiles | "Companion files changed since you trusted this build. Check them, then choose 'Trust this build' in the tray." |
| InternalError | "Something went wrong inside the companion. I stopped; details are in the diagnostics folder." |

### 6.10 Journal (`IJournal`)
- **Format.** JSONL, one file per day, in RuntimeDir\journal.
- **What is written.**
  - Every decision: outcome, class, action, counters and the step's state hash.
  - Every wait start and change (one record per change).
  - Every input summary.
  - Slide marks, written after the slide-back.
  - Incidents, HALTs, parks and stops.
  - Auto-collect status before and after each procedure.
  - Feature load reports and config changes.
  - Watchdog stats.
- **Field values.** Codes only (the regex documented on `JournalRecord`). Never write raw log lines, window titles, chat or role ids.
- **Robustness.** The writer never throws into control flow. Nothing is written while the window is displaced: GameIO keeps marks in memory and flushes them after the slide-back.

## 7. Inputs and targets

### 7.1 Target rules
- **Every click target is a named template.** It carries an expected client centre, search radius (≤ 40), threshold (≥ 0.85), margin (≥ 0.05; default 0.10), mask and status.
- **Capture and locate right before every click.**
  - Two captures must agree within 2 px.
  - Ambiguous (low margin) or not found means PreconditionLost; found outside the radius means LayoutChanged (HALT).
  - GameIO verifies the template once more in a capture taken WHILE SLID, within 2 px of the target point, just before DOWN. One narrow exception for cross-monitor slides of fixed HUD navigation targets (EVIDENCE c13): see "Cross-monitor NavFixed rule" in 7.3. One for anchor + offset presses whose anchor slid off the drawn monitor (the press point is checked instead): see "Off-screen anchor patch rule" in 7.3.
- **Raw coordinates are never clicked.** No stale coordinates and no fixed points. A grid cell is clicked only as a located anchor template + a fixed offset (`ClickAction.Offset`); no anchor found, no click.
- **Provisional templates are never clicked.** They may only be used for HUD classification.
- **Toggles** are clicked only when 2 stable captures say the HUD is in the opposite state.
- **Deny zones** (8.3) are a guard only:
  - a click point inside a zone inflated by 8 px is refused;
  - a target within 120 px of a zone needs 1.5 s of stillness and aborts on any cursor movement.

### 7.2 Key batch (`PressKey`, proven Send-GameKey timings)

| Step | Action | Guard |
|---|---|---|
| 0 | Readiness for a key (gates 6.3 1-12 are checked by the engine; GameIO re-checks identity, foreground PID, iconic, layout = baseline, key whitelisted and not denied, pacing token) | NotSent / Refused |
| 1 | Log position, then the `FocusOnSequence` via SendMessageTimeout (SMTO_ABORTIFHUNG\|SMTO_BLOCK, 250 ms each, every result checked) | timeout → AbortedAfterFocus (unfocus) |
| 2 | Wait until `RecoverFocus` is logged after the mark (≤ 1000 ms) and at least 300 ms have passed | FocusFailed |
| 3 | Re-check foreground and iconic | UserTookOver (no unfocus) |
| 4 | Post KEYDOWN (lParam `KeyDownLParam`), wait 70 ms, post KEYUP (`KeyUpLParam`). KEYUP is always posted once KEYDOWN was | PostMessage false + error 5 → IntegrityMismatch |
| 5 | Wait ≤ 700 ms (ends early when an expected UI is logged) | |
| 6 | `FocusOffSequence`, unless the game PID is really foreground | |

### 7.3 Slide click (`SlideClick`, port of p0_c3 plus the adopted findings)

**Geometry.** With home window rect H, client origin O (from ClientToScreen, read live), resting cursor C and target client point T:
- slid position = (C.x − T.x − (O.x − H.left), C.y − T.y − (O.y − H.top));
- c3 run 3 check: C=(918,672), T=(1748,314), offset (8,31) gives (−838,327).

| Phase | Action | Guard (any failure means abort per 7.4) |
|---|---|---|
| P0 | Readiness (all passive): see the list below | NotSent |
| P0 | Write the slide marker `run\slide.json` {pid, hwnd, processStartUtc, home, slid, utc} with Flush(true). Start the watchdog: dedicated thread, high-resolution waitable timer, target period ≤ 2 ms | |
| P1 | Record the log position, then send `FocusOnSequence` (SendMessageTimeout 250 ms each) | timeout → abort |
| P2 | Wait for `RecoverFocus` (≤ 1 s) and ≥ 300 ms. Re-check cursor (unchanged), cover (same HWND and rect), foreground and iconic | FocusFailed / AbortedAfterFocus / UserTookOver |
| P3 | `SetWindowPos(game, 0, slid.x, slid.y, 0, 0, 0x0215)`; time the call (> 200 ms is an incident record). Verify the rect equals the slid rect with the same size and maximized flag, and that TopAt(cursor) ≠ game | SlideDidNotLand / GameUnderCursor |
| P3 | Settle 150 ms, then PrintWindow on a worker thread (≤ 250 ms) and `IVision.VerifyAt(template, T, 2 px)` (a failed check may be skipped only under the cross-monitor NavFixed rule below, or replaced by the off-screen anchor patch rule below). Log popup gate: no Stack/Forbidden/Social/Busy/Unknown show, no `PopupRealShow` and no Pay line since `LocatedAtLog` (overlays ignored) | SlidCaptureFailed / SlidTemplateMismatch / LogGatePopup |
| P3→P4 | Post WM_MOUSEMOVE(T), wait 50 ms, check the watchdog state (no trigger), post WM_LBUTTONDOWN(MK_LBUTTON, T) | |
| P4/P5 | Wait 70 ms, then post WM_LBUTTONUP(T) | |
| P6 | Wait ≤ 400 ms (ends early on an expected UI). `FocusOffSequence` while still slid, so the LostFocuse raycast is taken at the slid position (skipped if the game PID is foreground). Wait ≤ 300 ms for the PressBlock | |
| P7 | `SetWindowPos(home, 0x0215)`, verify home, delete the marker, stop the watchdog, then journal the marks and stats | Incident → restore ladder (7.6) |

P0 readiness checks:
- cursor still for ≥ 0.8 s (1.5 s near a deny zone), with GetCursorPos returning true;
- no mouse button down;
- system idle ≥ 2 s;
- valid cover (7.5);
- slide plan passes the monitor, exposure, DPI and distance rules (7.5);
- home valid (7.6);
- game responsive: not IsHungAppWindow, WM_NULL round trip ≤ 200 ms;
- no jank or open scene_load in the last 2 s;
- locate age ≤ 1.5 s; for a press opted into the off-screen anchor patch rule whose anchor will lie off the drawn monitor once slid (anchor + cursor − press), locate age + 450 ms (FocusSettle + SlideSettle, the shortest time to the slid capture) ≤ 1.5 s, else NotSent `LocateTooOld` (detail `patch.locatedTooOld`) with nothing sent, and the engine locates afresh on its retry;
- pacing token (slide counts);
- stop switch clear.

Typical displacement is about 1.0 s (c3 PASS: 0.84 s). Budget 1.2 s (journaled when exceeded); dead-man at 1.5 s.

**Cross-monitor NavFixed rule (EVIDENCE c13, 2026-10-14).** When the slid window's largest overlap is on another monitor, PrintWindow returns only the part of the client on that monitor; the home-monitor part, where the target sits under the cursor, is black, so the slid check can never pass. GameIO may then press WITHOUT a passing slid check only if ALL of these hold (otherwise it aborts with SlidTemplateMismatch, as before):
- the plan is cross-monitor (`SlidMonitor` ≠ `HomeMonitor`, pure largest overlap);
- the engine opted in (`ClickRequest.NavFixed`): the template is tagged `NavFixed`, is clickable, is not Commit (tag or ui-map safety) or Forbidden, has no gate and no anchor, the step is not a Commit step and has no `ClickAction.Offset` (favourites slots keep the strict check);
- GameIO's own catalog check agrees (`ITemplateCatalog`: Verified and clickable, NavFixed, not Commit/Forbidden, gate None, no anchor), and the click point is the template centre or an aim inside it (|dx| ≤ 18, |dy| ≤ 10 from `TemplateCenter`);
- the slid capture shows the c13 signature: the verify centre lies on the home monitor, the 56x56 square around it is ≥ 98 % black (max channel ≤ 8), and the client part on the slid monitor has ≥ 2 % non-black pixels. A capture failure, `NotClickable`/`TemplateMissing`, a drawn-but-different target or an all-black capture still abort.

**Off-screen anchor patch rule (live run 2026-10-14, ScanRecipeList card 6, job 35e59bca).** An anchor + offset press (a recipe-list card, a favourites cell, the press point of the list drag) is verified at its ANCHOR. The slide that puts a far press point under the cursor can push the anchor off every monitor, and PrintWindow draws only the client part on the slid monitor (c13), so the anchor is black and the check can never pass: card 6 (press (364,669), anchor (485,123), cursor (1327,477), slide dx 963 dy −215, anchor at screen y about −69) aborted 30 times with SlidTemplateMismatch until BudgetExceeded. GameIO may then press on the evidence of the press point itself only if ALL of these hold (otherwise it aborts with SlidTemplateMismatch, as before):
- the engine opted in (`ClickRequest.PatchVerify` / `DragRequest.PatchVerify`, passing the located capture as `LocatedFrame`, in memory only): the template is tagged `ScreenAnchor`, is clickable, is not Commit (tag or ui-map safety) or Forbidden, has no gate and no anchor of its own; the step offset is larger than an aim (|dx| > 18 or |dy| > 10, `HardRules.MaxAimOffsetXPx`/`YPx`); the press is not near a deny zone and not a commit step; neither the root nor the current procedure commits or is gated (ScanRecipeList and ScanFavorites opt in, also inside a Craft job since 2026-10-15: the job kind does not decide, the find walk ends only on the card whose log tip names the wanted item; CraftLoop's favourites cells keep the strict check because crafting.loop is a gated commit procedure that picks what Make crafts);
- GameIO's own checks at P0, before anything is sent: the catalog agrees (Verified and clickable, ScreenAnchor, not Commit/Forbidden, gate None, no anchor), the press is farther than an aim from `TemplateCenter`, not near a deny zone; the located frame is one of GameIO's own home captures (by reference), the one `LocatedAtMonotonic` and `LocatedAtLog` name, and `VerifyAt` finds the anchor at `TemplateCenter` in it (2 px);
- the slid check failed with BelowThreshold or FrameInvalid (OutsideRadius or LowMargin mean the anchor is drawn elsewhere: abort); a capture failure or `NotClickable`/`TemplateMissing` abort as before;
- the anchor is hidden by the capture gap: its centre lies off the slid (drawn) monitor and the off-monitor part of the 56x56 square around it is ≥ 98 % black (drawn there = a real mismatch);
- the press patch, the 48x48 client square around the press point, matches: the located capture is at most 1.5 s older than the slid capture (measured at the slid capture, not only at P0; P0 already sends nothing when that cannot hold, see the readiness list); the slid client origin equals the located origin + (dx, dy) and puts the press point exactly under the cursor; the patch lies inside the client and inside the drawn monitor (Right/Bottom exclusive); in both captures it is at most 10 % black with a luma standard deviation ≥ 8; |mean luma slid − located| ≤ 16 (dim overlays, to which NCC is blind); zero-mean NCC ≥ 0.95 at the SAME client position (no search: a list that moved fails).

A drag compares its press point only; the release point and the steps are never compared (the list moves by design). With the opt-in on a press whose slid check PASSED, the patch comparison also runs in shadow (journal only, it never decides), so live scores calibrate the thresholds. The rule adds no message, no cursor move and no foreground change: it is pure computation on the existing captures.

Everything else is unchanged: located twice at home ≤ 1.5 s before, the log popup gate since `LocatedAtLog`, cursor unchanged and still covered, the window-rect watchdog, MOVE/DOWN/UP and the unfocus/slide-back sequence. The Input journal record carries `verify` (`ok`, `skipped.crossMonitor.navFixed`, `patch.anchorOffscreen`, `mismatch`, `notClickable`, `captureFailed`), `navFixed` (`allowed` or the refusal code), `patch` (`allowed` or the refusal code; a P0 located-frame refusal is journaled even when the run ends before the slid check; null without the engine's opt-in, or when the rule was not reached: an abort before the slid check, `notClickable`, `captureFailed`) and the measured `patchScore` (NCC, 3 decimals), `patchStd` (the smaller standard deviation), `patchDelta` (mean difference) and `patchAgeMs` (null when not measured); the engine's Input record carries `patchVerify`. `NavFixed` is set in `templates	emplates.json` on `hud.bag`, `hud.toggle.collapsed`, `hud.toggle.expanded`, `menu.ability` and (2026-10-16) the Ability view's right-column tabs `gather.tab`, `craft.make.tab` and `stock.tab` only; any other press whose slide would be cross-monitor is not ready (`NotReadyReason.CrossMonitor`: the engine waits for the cursor instead of failing its slid check, at most `HardRules.UnreachableLimit` (45 s), then the procedure ends Deferred: see FALLBACKS.md); the template catalog refuses it with Commit/Forbidden/gate/anchor, and features may not declare it. `tools\Companion.CaptureProbe` (operator-run, never by the host or tests) measures whether Windows.Graphics.Capture sees the home-monitor part while slid, as a possible future replacement for this rule.

### 7.3a Slide drag (`SlideDrag`, port of p0_c14, 2026-10-14)
Proof c14 scrolled the Make tab's recipe list about 300 px by dragging with the window instead of the cursor: the window came back home and the game was never foreground. `SlideDrag` runs the slide click's section unchanged up to the slid capture check and the popup gate (readiness, pacing, marker, watchdog, focus, P2 re-check, slide 0x0215, settle, PrintWindow check of the anchor at its own centre or, on the engine's opt-in, the off-screen anchor patch rule of 7.3 at the press point, popup gate); the release point is checked for readiness too (same cursor, cover and home, and the same slid monitor as the press, else NotSent `drag.monitorChange`). Then:

| Step | Action |
|---|---|
| press | Post WM_MOUSEMOVE(0, start), wait 40 ms, post WM_LBUTTONDOWN(MK_LBUTTON, start), wait 120 ms |
| steps | n = ceil(max(abs dx, abs dy) / 20) steps (at most 20, 400 px). Per step: `SetWindowPos(slid moved by minus the step, 0x0215)` (not under the message gate; an abort's slide-back during the call re-issues home), verify the rect landed (else SlideDidNotLand), TopAt(cursor) is not the game, post WM_MOUSEMOVE(MK_LBUTTON, the moved client point), wait 45 ms |
| release | hold still 250 ms (no fling), post WM_LBUTTONUP at the point under the cursor, wait 250 ms, fake unfocus while slid, slide back 0x0215 |

- The watchdog accepts the previous or the new rect while a step's move is in flight, ends the drag on ANY cursor movement or user mouse button before the release, and uses the drag's own dead-man `HardRules.DragDeadManDeadline(n)` = 1.5 s + 120 ms + n x 45 ms + 250 ms (15 steps: 2.545 s), with the section deadline raised by the same amount.
- Every abort of a drag releases the button FIRST (UP at the client point under the cursor now), then slides home: cursor moved / user button / stop / main-thread aborts: UP, 20 ms, slide back, unfocus; game foreground: UP, async slide back, no unfocus; dead-man: notify-unfocus, UP, async slide back; window changed: UP, unfocus, ladder.
- The slide marker records the path (`pathEnd` = the last step's rect): crash recovery and `RestoreHomeIfOurs` treat any rect on that straight path (within 1 px) as ours.
- The choke point allows WM_MOUSEMOVE with wParam 0 or MK_LBUTTON (drag steps) and nothing else.
- The Input journal record carries `input=drag`, `dragSteps`, `dragStepsDone`, `dragDx`, `dragDy`; `DragReport` gives the press as a `ClickReport`, the steps planned and done and the release point (`Completed` = sent, no abort, all steps, UP).

### 7.3b Foreground mode (the user's decision 2026-11-02, proof p0_c15)
Proof c15 (2026-11-02 01:53, game foreground and maximized, cursor resting on it): a slide to (-759,36) landed in 16 ms, the game stayed maximized and foreground, the clip did not change; a posted MOVE/DOWN/UP with no focus message opened the Make view, the window was home after 764 ms; a posted Esc with no focus message closed it. The user accepted the trade-off: the window jumps visibly under the mouse for about half a second per press, and a click or key the user makes during that time goes into the game. Scope: every input (clicks, Make, drags, keys). Switch: config `input.foregroundSlide` (default on); off restores the old wait.

| Part | Foreground mode |
|---|---|
| Mode | Chosen by readiness and fixed for the whole input (`InputMode`, journaled as `mode` on Input and Slide records, reported on `ReadinessReport`, `ClickReport`, `KeyReport`). Foreground mode only when the game's own HWND is the foreground window (a game dialog in front stays GameForeground). |
| Readiness | Game in front 5 s without a break (`HardRules.ForegroundSettle`) and no foreign RecoverFocus for 5 s; the game's log not "LostFocus"; no mouse button; cursor still 2 s; no input (GetLastInputInfo) for 2 s (`ForegroundSystemIdle`); the game under the cursor and at the 8 probe points, the cursor at least 16 px inside the client (`ForegroundRules`; else CursorNotOnGame). Keys: no button, no input for 3 s (`ForegroundKeyIdle`), a letter key not while UI_CHAT is open (ChatOpen). New reasons: CursorNotOnGame (bounded like NoCover), ForegroundSettling, UserInputRecent, ChatOpen (user active). |
| Click | No P1/P2 (no focus message; the FocusSent mark only opens the engine's event window). P2 re-check: still the foreground HWND (else ForegroundLost), the GetLastInputInfo tick unchanged since readiness (else UserInput), the game under the cursor. P3 slide as 7.3; after it the game must be under the cursor (else CoverChanged). P4-P5 as 7.3. P6: no unfocus and so no press block; instead the drain: a WM_NULL round trip (the pump has begun: a thread gets its sent messages before its posted ones, so our DOWN/UP are read in this pump), 50 ms (`ForegroundDrainSettle`), and a second round trip, answered only at a later pump, after the frame that read them ran its input at the slid position (c6: a button read with the slide-back hit-tests at home). A game that does not answer within 2 s goes to the abort's drain (below). Then P7. |
| Drag | As 7.3a with the game required under the cursor after every step, and the drain instead of the unfocus. **Off until proof c16 passes** (config `input.foregroundDrag`, default false; `ReadinessQuery.Drag`): with the game in front, Windows sends the game its own mouse moves with the real (released) button state while the window steps under the still cursor, and whether the game then ends the drag as a click is not proven. Until then a drag with the game in front is GameForeground (the old wait). |
| Key | No focus message, no unfocus; KEYDOWN only if the game is still the foreground HWND and the input tick is unchanged since readiness; input during the key or its wait is reported (`KeyReport.UserInputDuring`). |
| Choke point | `EnterForegroundInput`: any focus message (Send or Notify) during the input is a HardRuleViolation. |
| Pacing | The focus-pair bucket is not used (no focus pair). |
| Engine | "Game foreground" alone is not user activity: gate 7, the commit gate and HudClean use `ReadinessReport.SinceGameUse` (time since keyboard or mouse input while the game was in front, or since it came to / left the front). No RecoverFocus check (FocusLogged stays false by design). No press block: a delivered press without a result is "nothing happened" (counted), never the uncounted PressNotRegistered repeat; a toggle is judged by the HUD read. The user's input overlapping our input (GameIO's report, or the game-use clock moving after DOWN/KEYDOWN within the attribution window) is checked before anything is attributed: a pay line is a HALT without Esc, a forbidden UI waits as foreign (no Esc), the expected UI is success, anything else is UserActive (wait, then resume from the latest step whose precondition holds; the user's game use invalidates the clean HUD; a commit job defers). The user's click on the slid game before our DOWN (it hits our target) is UserActive too, never a repeated press; a switch-away after our DOWN (or KEYDOWN) lets the log decide. The reset Esc, the closing Esc and the quick click's late read treat the user's input around them the same way (no EscOpenedUi or ForbiddenUi HALT, no Esc on the user's window). ForegroundLost and UserInput map to UserActive; ChatOpen is a bounded wait (NotSent: a stale chat entry in the log must not hold a letter key forever). Note: any mouse movement during the jump counts as the user's input, so a commit job then defers. |

### 7.4 Watchdog triggers and abort orders
The watchdog runs as an IDisposable instance per call. It samples about every 1-2 ms from the start of focus until slide-back:
- GetCursorPos (return value checked);
- GetAsyncKeyState for VK_LBUTTON, VK_RBUTTON and VK_MBUTTON;
- TopAt(cursor) and the cover HWND/rect;
- the foreground PID;
- IsIconic;
- the window rect and IsZoomed;
- GetClipCursor and GetCursorInfo;
- the stop switch;
- the deadline.

Exactly one owner performs the slide-back (`Interlocked.CompareExchange`); the watchdog can act on its own.

| Trigger | Before DOWN | After DOWN |
|---|---|---|
| Cursor moved / button down | Slide back (move-only), then unfocus → AbortedAfterFocus | Recorded only (P4 movement → Uncertain). For FireOn.Up targets, movement before UP uses the forced order |
| TopAt(cursor) == game / cover changed / stop | Slide back at once (0x4215 from the watchdog), then unfocus | Post UP if pending, wait 20 ms, slide back, unfocus (PAIR-17) |
| Game PID foreground | Slide back at once (async), NO unfocus → UserTookOver | Slide back at once (async), post UP if pending, NO unfocus → UserTookOver |
| Iconic | No SetWindowPos, no posts → UserTookOver (restore later via `RestoreHomeIfOurs` once restored) | Post UP if pending; no SetWindowPos; no unfocus |
| Rect/zoom changed not by us | Restore ladder → Incident (SelfResize = HALT) | Post UP if pending, unfocus, restore ladder |
| Clip cursor or cursor visibility changed | Unfocus (unless foreground), slide back → Incident CursorClip (HALT) | Post UP if pending, unfocus, slide back → HALT |
| Dead-man (1.5 s) / SendMessageTimeout timeout / hung | **Forced order:** `SendNotifyMessage` FocusOff (nonqueued, so a pending posted DOWN is ignored, per c6), then `SetWindowPos(home, 0x4215)` | Same forced order, plus post UP if pending; outcome Uncertain, settle 2 s |

**Foreground mode (7.3b).** The table changes as follows: the game no longer being the foreground HWND (including 0) is ForegroundLost: slide back at once (async), UP if pending, never a focus message → UserTookOver. The game must stay under the cursor (else CoverChanged); a GetLastInputInfo change with the cursor still (a key, the wheel, a button) is UserInput; any mouse button at any phase is UserButton; both are checked before the window rect, so a click on the slid caption slides home instead of fighting the user's move. Every order after DOWN, ForegroundLost included (the game may read our DOWN before the deactivation), posts the pending UP, then drains (two WM_NULL round trips and 50 ms), and only then moves the window (slide back, async for the dead-man and ForegroundLost; the ladder for a changed rect), because no fake unfocus makes a late button harmless. A game that stopped responding keeps the window slid (it is frozen anyway; a move would only be applied in the same pump as our DOWN) for up to 30 s (`ForegroundDrainHangMax`; the watchdog join waits as long); past that the window goes home (R3) and the input is an incident `UndrainedSlideBack` = HALT `GameStoppedMidPress`, never retried. FinishClick's safety net drains too before it moves the window, and neither the abort's moves nor the ladder run while our press may be unread (`InputSession.MoveBlocked`), nor on a minimized game. Only a timeout is waited out: a game window that disappears ends the drain at once (its queue died with it; the input is GameGone), any other refused send gives up at once (HALT). The slide marker of a foreground-mode input says so (`foreground`): crash recovery (the guardian, the next start) drains first, up to 10 s (`ForegroundRecoveryDrainMax`), and otherwise keeps the marker and the window (`marker.pressPending`). Before DOWN: slide back only. Iconic: UP only. A section that slid counts as touched (AbortedAfterFocus) even with zero focus messages. The restore ladder's posted-WM_SIZE rung sends no unfocus inside a foreground input.

**Guardian.** If the host process dies while displaced, the guardian child restores the window from the marker (10.1).

### 7.5 Cover, monitor and exposure rules (F11, F12, WIN-4, WIN-10, PITFALL-04/11)

**Valid cover.** The top-level window under the cursor (`WindowFromPoint`, then `GetAncestor(GA_ROOT)`) must be:
- visible, not cloaked, not minimized;
- not topmost, not a tool window or NOACTIVATE, not layered with alpha < 255;
- not of class Shell_TrayWnd, Shell_SecondaryTrayWnd, NotifyIconOverflowWindow, Windows.UI.Core.CoreWindow, XamlExplorerHostIslandWindow, MultitaskingViewFrame, ForegroundStaging, tooltips_class32, #32768 or Ghost;
- not owned by the game PID or its children (GA_ROOTOWNER ≠ game; the exe name of the owner PID is not TuanjieCrashHandler64.exe or a TOS child);
- not a companion window;
- the foreground window, or owned by the foreground process.

The cursor must also satisfy:
- it is at least 16 px inside the cover's DWMWA_EXTENDED_FRAME_BOUNDS;
- the same root is found at 8 points ± 12 px around it;
- the cover rect was stable for the whole stillness period;
- the cover is above the game in one EnumWindows z-order snapshot (index compare; walk capped at 2048).

**Monitors.** EnumDisplayMonitors is re-read before each slide; nothing is hard-coded. All of these must hold:
- MonitorFromRect(slid, NEAREST) (pure largest-overlap computation, and the Win32 value) is the home monitor, or another monitor whose DPI is 96 (user decision 2026-10-14 after proof c12: on a same-DPI monitor the game keeps its size and maximized state and only re-targets its frame rate to that monitor's refresh rate, reverting ~2.6 s after the slide back; a DPI change would make Windows resize it). Each click journals `crossMonitor` and `slidMonitor`;
- (the old "slid centre inside the home monitor" rule was dropped, live run 2026-10-14: a window slid past the top or bottom edge where no monitor exists keeps its monitor, and the rule made every lower-half target unreachable with the cursor resting near the top);
- |dx| and |dy| are ≤ 2000;
- the DPI of the home monitor and the cursor's monitor is 96.

Otherwise NotSent (MonitorRule).

**Exposure.** Exposed area = (slid rect ∩ the union of monitors) − the union of the frame bounds of all visible windows above the game. It is journaled per slide (`exposedPx`), not refused: a zero-exposure rule made every useful slide impossible on the user's layout (live run 2026-10-14); the user's rule is that the game never shows under the cursor, which the cover probes enforce. In foreground mode (7.3b) the whole slid window is visible by design.

### 7.6 Home rect, restore ladder, crash recovery (F24, WIN-9, PITFALL-02/23)
- **Home.** Home is the rect sampled right before each slide. It is valid only if all of these hold:
  - it has been unchanged for ≥ 1 s (sampler);
  - IsZoomed;
  - the client is 1920x1009 and matches the last `Current screen resolution` line;
  - it equals the live maximized rect of its monitor (work area plus frame);
  - WS_CAPTION is set.
- **Restore.** Only deviations that started inside our displacement (or that a matching marker proves) are restored. Every other deviation means DeviationOwner.Others: pause and notify. Never fight the user. A new home is adopted only after re-validation.
- **Ladder:**
  1. Move-only to home (0x0215, or 0x4215 from the watchdog). Verify within 200 ms.
  2. Only if the size changed during our displacement, and at most once: move+size to the live maximized rect (0x0214).
  3. One posted `WM_SIZE(SIZE_MAXIMIZED, 1920x1009)`. Wait ≤ 2 s for `Current screen resolution 1920 1009` and rect = home.
  4. All failed → HALT RestoreFailed.

  No rung runs while the window is iconic. If the home monitor has vanished, do not restore: re-baseline and notify.
- **Crash recovery** (`RecoverLeftoverDisplacement`, at host start and in the guardian):
  - marker present, PID/HWND/start time match and rect = slid rect → move-only to the marker's home, verify, journal `LeftoverFromCrash`, delete the marker;
  - identity differs → delete the marker only;
  - rect already equals the marker's home (died before or after the slide) → delete the marker only;
  - rect matches neither → notify and leave the window alone.
- **Prefs.** After every slide and at start, compare `ReadWindowPrefs()` with the baseline (fullscreen mode 3, position, monitor, resolution). A difference is incident `PrefsChanged` plus a notification. These values are never written.

### 7.7 HUD toggles
Toggle templates per state:
- `hud.toggle.collapsed`: the four-petal icon, visible when collapsed; clicking it expands the menu.
- `hud.toggle.expanded`: the X, visible when expanded; clicking it collapses the menu.

**HUD signature:** `hud.bag` plus one of the toggles at its expected place. Captures used for a toggle decision must be ≥ 300 ms apart and agree.

**The menu stays EXPANDED for good** (live run 2026-10-14: the far-right toggle is often out of reach of the user's resting cursor, and cross-monitor slides cannot be verified, c13; the toggles are NavFixed, so a cross-monitor toggle press now goes ahead under the 7.3 rule). **Calibrate** captures the strip as found and expanded, and ends expanded; no procedure collapses or restores the menu except the explicit `CollapseMenu` job.

## 8. Navigation

### 8.1 ui-map.json (`UiMap`)
- **Screens** carry: id, log UI name (if any), anchor templates, isHud, HUD state, status, escCloses.
- **Buttons** carry: id, screen, template OR key, leadsTo, expectedUi, safety (Navigation, Toggle, Commit, Forbidden), gate, status.
- **Edges** go forward from a screen. Leaving any `escCloses` screen goes through Reset (Esc until nil), then the HUD state is re-identified. Esc buttons are not modelled.

### 8.2 Navigator
- **`Identify(frame, ui)`:**
  - The advisory top UI equals a screen's `LogUiName` and its anchors are found → Confirmed.
  - Anchors are NeedsSurvey → LogOnly.
  - The top is nil (HudClean) and a HUD state is classified → VisionOnly (`hud.collapsed`/`hud.expanded`).
  - Anything else → Unknown.
- **`FindPath`:** BFS over buttons, skipping Forbidden and Commit. NeedsSurvey or Provisional hops make the result `NeedsSurvey`. Ties are broken by button id (ordinal).
- **`NavigateTo`:**
  1. Identify. If the screen is Unknown: Reset, then identify again; still Unknown is PreconditionLost.
  2. Run `BuildProcedure(path, startWithReset: current screen is not a HUD state)` through `IProcedureRunner`.
  3. Each hop is a step: pre = the hop's screen; action = key or click; expect = the leadsTo screen's log UI and/or anchors (or the HUD state for toggles).
  4. Arrival is confirmed by identification.

### 8.3 Seed map (engine-nav writes `data\ui-map.json`)

| Screen id | Log UI | Anchors | HUD | Status | escCloses |
|---|---|---|---|---|---|
| hud.collapsed | — | hud.bag, hud.toggle.collapsed | Collapsed | Known | no |
| hud.expanded | — | hud.bag, hud.toggle.expanded | Expanded | Provisional | no |
| bag | UI_BAG_MAIN | (needsSurvey) | — | Known (log only) | yes |
| ability | UI_LIFESKILL_MAKE_VIEW | (needsSurvey) | — | Provisional | yes |
| lumi_hut | UI_HOME_PET_VIEW | (needsSurvey) | — | NeedsSurvey | yes |
| craft_station | UI_LIFE_ALCHEMIST_MAKE_POT | — | — | NeedsSurvey | no (never Esc during a craft) |

| Button id | Screen | Template / key | leadsTo (expected UI) | Safety | Status |
|---|---|---|---|---|---|
| hud.expand | hud.collapsed | hud.toggle.collapsed | hud.expanded | Toggle | Known |
| hud.collapse | hud.expanded | hud.toggle.expanded | hud.collapsed | Toggle | Provisional |
| bag.key.c / bag.key.e | hud.collapsed / hud.expanded | key B | bag (UI_BAG_MAIN) | Navigation | Known |
| bag.click.c / bag.click.e | hud.collapsed / hud.expanded | hud.bag | bag (UI_BAG_MAIN) | Navigation | Known |
| menu.ability | hud.expanded | menu.ability | ability (UI_LIFESKILL_MAKE_VIEW) | Navigation | Provisional |
| menu.homestead | hud.expanded | (none yet) | lumi_hut (UI_HOME_PET_VIEW) | Navigation | NeedsSurvey (c3 run 1 hit it at client (1732,444) with a verified aim; re-measure with Calibrate) |
| station.commit | ability | (none) | craft_station | Commit (gate crafting) | NeedsSurvey |

**Forbidden keys.** Y (Recharge = purchase), I (server login) and Enter (chat) are hard-denied in code. Other mapped keys (t, u, o, g, h, j, k, n, m) are not whitelisted, so they are not modelled.

**Deny zones** (client coordinates, approximate; the vision-ocr builder verifies them by viewing the images):

| Zone | Rect | Source |
|---|---|---|
| hud.market | (1600,0)-(1680,100) | c3b crop x+1440 |
| hud.event | (1485,0)-(1570,100) | c3b crop x+1440 |
| hud.whatson | (1595,115)-(1690,235) | c3b crop x+1440 |
| hud.timed_offer ("Hyperwe 23:41") | (1590,280)-(1695,400) | c3b crop x+1440 |
| hud.left_offers ("Limited-time", "CoC Super") | (300,0)-(580,170) | c1 half scale ×2 |
| chat_panel | (660,590)-(1270,1009) | c1 / c3b (also masked in captures) |

## 9. Procedures (engine-nav writes `data\procedures.json`, `ProcedureDefinition[]`)

| Id | Kind | Steps (id: action → expect) | Limits / notes |
|---|---|---|---|
| Reset | Navigation | reset: `ResetAction(10)` → HideTop(nil) | MaxEsc 10 |
| OpenBagKey | Navigation | reset: Reset → HideTop(nil); open: `KeyAction(B)` (Pre HudClean, CompensateOnPark) → Opened(UI_BAG_MAIN); close: `KeyAction(Escape)` → HideTop(UI_BAG_MAIN) | default |
| OpenBagClick | Navigation | reset; open: `ClickAction(hud.bag)` (Pre HudClean + TemplatesPresent[hud.bag], CompensateOnPark) → Opened(UI_BAG_MAIN); close: Esc → HideTop(UI_BAG_MAIN) | NearDenyZone (timed-offer zone ≈ 55 px away) gives 1.5 s stillness |
| ExpandMenu | Navigation | reset; toggle: `ToggleHudAction(Expanded)` (SkipIfSatisfied) → Hud Expanded | |
| CollapseMenu | Navigation | reset; toggle: `ToggleHudAction(Collapsed)` (SkipIfSatisfied) → Hud Collapsed | explicit use only: the only procedure that collapses the menu (2026-10-14) |
| OpenAbility | Navigation | reset; expand: Toggle(Expanded); open: `ClickAction(menu.ability)` (Pre Hud Expanded + HudClean) → Opened(UI_LIFESKILL_MAKE_VIEW), timeout 6000; the view is left open | `menu.ability` is provisional, so the pre-flight returns NeedsSurvey ("run Calibrate, cut menu.ability") with zero inputs |
| Calibrate | Maintenance | reset; initial: `CaptureAction("initial", HudStripRight)`; expand: Toggle(Expanded) SkipIfSatisfied; cap-exp: Capture("expanded", HudStripRight). Ends EXPANDED (2026-10-14: the collapse, cap-col and restore steps were removed) | Full-scale right HUD strip (client x 1440-1919) saved to RuntimeDir\calibration\<utc>\. The run is the user's opt-in for these captures. TemplateCutter then cuts `hud.toggle.expanded`, `menu.ability` and `menu.homestead` |

These are the core feature's jobs (no params). The `core` feature lives in Companion.Features and is always enabled.

## 10. Host

### 10.1 Process model
- **Companion.App:**
  - WinForms tray app; `app.manifest` with requireAdministrator, PerMonitorV2 DPI awareness and Windows 10+.
  - One UAC per session; no console.
  - Started by the user (Start-menu shortcut to `dist\Companion.App.exe`).
- **Single instance:** mutex `Local\TOS-companion.host` (both instances are elevated). Clients discover the host through the pipe, never the mutex (PITFALL-20).
- **Guardian:** `Companion.App.exe --guardian <hostPid> <hostStartTicks>` is started with `Process.Start` (UseShellExecute=false). It inherits the elevated token, so there is no UAC. It has no tray and no pipe. It waits for the host process to exit, then runs `IGameIo.RecoverLeftoverDisplacement()` and exits.
- **Trust** (SEC-1, FR-01):
  - Features are compiled in: no `Assembly.Load`, no scripts, no plugin folder scanning.
  - `trusted-build.json` (RuntimeDir, admin-only) holds the SHA-256 of every install-side file, written only when the user clicks "Trust this build…" in the tray. The dialog shows the build hash and the file count; it is a WinForms form opened by the user, not a MessageBox.
  - Mismatch → HostState.Untrusted and every job is refused.
  - Data files are read only through `IFileTrust.ReadTrusted`, so they are verified as read.
  - Hard floors live in code, whatever the data says.
  - Residual risk, documented: code DLLs in a user-writable folder are loaded before the check. Optional: `build.ps1 -Install` copies `dist` to `%ProgramFiles%\TOS-companion`; this needs one elevation by the user.
- **RuntimeDir** (`%ProgramData%\TOS-companion`): the App creates it with a protected ACL. Before every write it checks for reparse points on the target folder, and writes with `FileMode.CreateNew` plus a rename (SEC-2).

### 10.2 Job channel (SEC-2, F28)
- **Pipe.** Named pipe `TOS-companion.jobs.v1`. PipeSecurity gives the interactive user's SID ReadWrite and denies network logons. One JSON message per line, ≤ 1024 bytes (`PipeProtocol`). Server reads have a timeout.
- **Request validation.**
  - Unknown fields and over-long values are rejected.
  - `jobId` must match `^[0-9a-f]{32}$`.
  - `jobName` must be in `IJobCatalog` (enabled features only).
  - Params are validated by `IJobCatalog.Validate`: typed and bounded, and never used in paths, code or regexes.
  - Handlers that need knowledge refuse (Refused, `knowledge.unavailable`) when `IJobContext.Knowledge` is null (the store could not open the profile).
  - Commit jobs need their automation switch on. Nothing depends on which character is logged in (4.33: the per-character binding was removed): every job uses the one knowledge profile (11.2); a request field `characterKey` is an unknown field.
  - Rate limits: ≤ 10 jobs per hour, ≤ 1 queued + 1 running.
- **Ops.** submit, status, result, cancel, stop (anyone may stop), pause, resume (not after Stop or HALT; those are tray-only).
- **Execution.** All jobs run on the single engine thread (the only game I/O queue). A character switch in the game does not cancel a job (4.33); the engine's own screen and log checks see the relog like any other interruption.
- **Results.** Kept in memory (the last 50) and journaled. Output file names are never derived from job data.

### 10.3 Tray (the only UI)
- Status line: state, job, wait reason, game connected.
- Pause / Resume.
- **Stop now** (also Ctrl+Alt+Pause).
- **Clear stop / Clear HALT…**
- Automation ▸ Crafting on/off, Homestead on/off, Slide clicks on/off. Each change asks for a confirmation in a tray form, is written to config.json atomically and is journaled.
- Auto-collect ▸ "status N means not gathering" (adds N to `policy.treatAutoCollectStatusAsIdle`).
- Policy ▸ "May close chat/mail windows" (`closeUserUisAtJobStart`).
- Knowledge ▸ enter or correct stock and recipes (UserEntered / userVerified).
- Crafting… (added 2026-10-14): a plain window listing the current character's recipes from its knowledge (read only; materials as "Material 1/2/3" with required and last observed owned counts). Start submits CraftLoop {slot from `screen:craft.fav/<n>` or 0, count or 0 = until materials run out, batch 1|max}, Scan recipes submits ScanFavorites, Stop cancels the running job (as the Cli's cancel). Requests take the pipe's whole path in process (`LocalJobChannel`: wire encoding, `PipeCodec` validation, `JobHost.Handle`), so the whitelist, gates and limits apply unchanged; refusals and end codes are shown in plain language. Status refreshed every second.
- Trust this build…
- Exit (stop, restore if needed, then exit).

Notifications are NotifyIcon balloons (non-activating). They are de-duplicated by code, and the text is fixed per code (6.9).

### 10.4 Heartbeat, diagnostics
- **Heartbeat.** A timer updates `HostStatus.HeartbeatUtc` every 1 s, independent of the engine thread. The Cli treats the host as dead if it cannot connect within 2 s or the heartbeat is older than 5 s.
- **Diagnostics bundle on HALT** (RuntimeDir\diagnostics\<utc>-<code>\):
  - halt.json (`HaltReport`);
  - attempts (codes);
  - the job's journal records;
  - a filtered log excerpt from job start to the HALT (≤ 500 lines, through `ILogTracker.ReadFilteredExcerpt`);
  - template-sized PNG crops (template rect inflated by its radius) from the last capture, with the chat panel blacked out.
- No full frames go into a bundle. At most 20 bundles and 50 MB; the oldest are deleted. Bundles stay local and are never uploaded.

### 10.5 Companion.Cli (non-elevated, asInvoker)
- **Commands:**
  - `status`
  - `jobs`
  - `submit <JobName> [name=value ...] [--wait] [--timeout <s>]` (generates the job id)
  - `result <id>`
  - `cancel <id>`
  - `stop`
  - `pause`
  - `resume`
- **Output.** Codes and short summaries only.
- **Exit codes:** 0 ok, 2 refused, 3 host not running, 4 halted, 5 timeout, 6 needsSurvey.

### 10.6 Solution and build.ps1 (assembler)
- `Companion.sln` holds all `src`, `features`, `tools` and `tests` projects.
- `build.ps1`:
  1. Sets the env vars from section 2.
  2. Runs `dotnet build Companion.sln -c Release`.
  3. Runs `dotnet test Companion.sln -c Release --no-build`; these tests are offline.
  4. Runs `dotnet publish src\Companion.App -c Release -r win-x64 --self-contained true -o dist`, and the same for the Cli into `dist`.
  5. Checks that `dist\data`, `dist\templates` and `dist\features\*` are present.
  6. Prints the build hash.
- `build.ps1` never starts the App and never elevates (`-Install` is the only optional elevated copy).
- `tools\Companion.CaptureProbe` is built (not published) and only ever run by an elevated operator with the host stopped: it moves the covered game window move-only by `--dx/--dy` through GameIO's internal layer (`Diagnostics\ProbeWindow`, no window messages), captures with PrintWindow and Windows.Graphics.Capture at home and at each `--delays` ms, moves it back (verified, repeated), and writes PNGs (chat blacked out via `IVision.EncodePng`) plus `report.txt`. Its only own P/Invokes are the two d3d11 device calls in `StaticRules.PInvokeAllowlist`.

## 11. Perception, Knowledge, Planner, Features, switches

### 11.1 Perception (Companion.Perception)
- **Readers are data** (`ReaderDefinition`).
  - Screen identity = log UI name AND anchors found in the same capture; otherwise ScreenMismatch.
  - Regions are client rects, optionally relative to an anchor.
  - Parse rules: Int, Pair (with semantics bound to the screen: OwnedRequired, StoredCapacity or AssignedSlots), Text, Abbreviated (stored as a range), ItemCellCount (empty = 1 only for a known stackable; `Lv\d+` = equipment, not stock) and IconFingerprint (added 2026-10-14: an item tile read without OCR; Text = "icon:" + a 256-bit fingerprint, Ok without text = an empty slot, Unknown when the tile outline is not at its place). Added later on 2026-10-14: TileRow (the tiles of a row are DETECTED in a band, relative thresholds, centred on the band, equal size and spacing, else Unknown; values `<region>` = tile count, then `icon.k` and `pair.k` per tile, the pair read in the 120x24 box under the tile), LabeledPair (a pair that ends a labelled line: the last word after the last gap wider than a quarter of the text height), PairSemantics.Progress (current ≤ target), and `RegionDefinition.Optional` (nothing drawn in the region = Ok without a value, detail "absent").
- **OCR** uses `Windows.Media.Ocr` (en-US), on a crop upscaled 3x, then 4x, then 5x (numbers: stops at the first agreement with the digit reader), with binarisation per font style. `Wait(timeout)` is always used, and a timeout gives Unknown (OCR-02). Text touching the region's left or right edge (a longer number or name) gives Unknown; Text (2026-10-15) also when ink lies within 16 px of the right edge (a scrolling recipe title is cut by its box anywhere, even between two words).
- **Digit templates** are kept per font style and cut only from full-scale captures, several instances per character (the same glyph renders 1 px wider or taller with its subpixel phase; a character scores its best instance). Built in (compiled, 2026-10-15, from the copies in `tests\TestData\images`: `make_fav1..5_full.png`, `live_ability_make_full.png`, `live_ability_view_full.png`): `parchment-pair` (Make tab pairs; real 0 1 3 4 5 6 7 9 /, borrowed 2 8 from the green description numbers), `badge-dark` (product owned badge; real 0 1 2 5 6, borrowed 3 4 8 9 from the bold stats and 7 from the announcement) and `counter-dark` (the counter under the product picture; real 0 3 5 6 /, borrowed 1 4 7 9 from the pairs and 2 8 from the descriptions). Every glyph records its provenance and whether it is borrowed; a provenance test re-cuts every glyph and a borrowed glyph is deleted once a real instance exists. Thresholds unchanged (0.75 per glyph, digits-only 0.80 / 0.25): every pair, badge and counter of the six captures reads at >= 0.89 with margins >= 0.35.
- **Accepting a value.** Accept only when OCR and the digit reader agree AND the semantic checks pass (regex per rule, plausible bounds, a pair's required side equal to the known recipe requirement). Otherwise re-read once with the other binarisation. **Digits-only fallback** (2026-10-14, Windows OCR misreads the small Make-tab digits at some scales): if no OCR pass agreed, every pass ran (no timeout) and none parsed to a different valid value, the digit-template read is accepted when every glyph scores >= 0.80 and >= 0.25 above any other character (detail `digits-only`, lower confidence). Otherwise Unknown. Never guess. Confidence is synthesised (OCR has none).
- **Seed readers** (`data\readers.json`; `craft.pairs` and `craft.product` are Ready since 2026-10-14, the others are `NeedsSurvey` with no coordinates):

  | Reader | Screen | Note |
  |---|---|---|
  | `bag.grid` | UI_BAG_MAIN | cells, counts, 3 tabs; scrolling unproven |
  | `craft.pairs` | UI_LIFESKILL_MAKE_VIEW + anchor `craft.materials.header` | Ready: `materials` (TileRow over client (940..1587, 674..763), centred on x 1263.5: 3 materials at x 1088/1220/1352, 4 materials at 1022/1154/1286/1418, 132 px apart, 87x89; full scale in `make_fav1..5_full.png`) giving icon.k (the value's rect = the tile) / pair.k (font `parchment-pair`); `requires` (LabeledPair, optional: the "Requires: 86/5" line some recipes show under the pairs, client about (1036..1340, 800..838); seen at half scale only, read with the pair font, absent on every full-scale capture); the "insufficient" colour is red and reads like the green |
  | `craft.product` | UI_LIFESKILL_MAKE_VIEW + anchor `craft.materials.header` | Ready: product.name (Text, client (1325..1580, 220..256): titles start at x 1326, a scrolled long title is cut at x 1325 and reads Unknown; the item tip's logged name is the real name source), product.owned (Int, badge, font `badge-dark`), `product.counter` (Pair Progress, optional, font `counter-dark`: the "5/300" in the bar under the stars of some products, client (1086..1206, 506..532), the text overlapping the bar's lighter outline; absent on White Toast and Lime Water) |
  | `craft.quantity` | UI_LIFESKILL_MAKE_VIEW | requested quantity before commit |
  | `lumi.products` | UI_HOME_PET_VIEW | stored/capacity such as 18/18; seen only in the half-scale c3_Ability.png |

### 11.2 Knowledge (one profile, 4.33)
- **Profile.** One knowledge file, `RuntimeDir\knowledge\<key>\knowledge.json`, for every character, whoever is logged in. The folder is chosen once and pinned in `profile.json`: the one the old `characters.json` allowed when it holds knowledge (so everything learned before 4.33 is kept as it was), else the largest knowledge folder, else `0000000000000000`. `characters.json` is only read while nothing is pinned, never written. Other characters' old folders stay on disk untouched.
- **Character.** The log still yields `CharacterKey` (salted hash of the last `req_login_role`; the raw token is never stored), but nothing gates on it: no permission, no re-key, no job cancel on a character switch. Before 4.33: Unknown/ReadOnly characters allowed only non-commit jobs, Commit needed Allowed, and a switch aborted the running job.
- **Stock entries.** `count | range`, observedAt, log position, source (observed, derived or user), confidence, coverage, stale, and (schema 2, 2026-10-15) the optional game item id.
  - Unseen items are unknown, never 0.
  - A bag scan is partial: visible tab and slots only, never "full".
  - Derived deltas apply only when their log offset is after the item's observation and after `LastAppliedOffset`, which is persisted in the same atomic write.
  - A rotation marks derived stock stale.
  - An autoeat line marks food stale.
  - Any user-foreground interval marks all stock stale.
- **Recipes** are learned only from the screen or from user entry. They record coverage, game build fingerprint, `userVerified` and `RecipeConfidence` (and, from the Make tab since 2026-10-14, the requirement code `requires:<required>` of a "Requires" line and the optional `Counter` "a/b" under the product picture, informational, not content; since 2026-10-15 the optional product item id and material item ids). Never from game files, registry item ids or server dumps.
- **Item names and ids from item tips** (2026-10-15, user decision). When the companion presses an item icon on the Make tab, the game opens UI_ITEM_TIPS_PASS_TOUCH and logs `item_tips_mgr ... print name, uuid, id`; only a tip logged after that press is used. The product's name and id replace the OCR title; a material icon's item is stored as an icon item (icon fingerprint → item id + name, `IconItem`) and every stock entry and recipe material keyed by that icon moves to the item's name key and id. A known icon is never pressed again.
- **Item identity** (`ItemIdentity`): two items are the same when both know the item id and the ids are equal, else when their keys are equal. An observation with an id moves the entry with that id from another key; a recipe with a product item id replaces any other recipe with that id; recipe content compares materials by identity.
- **Schema.** Character files are written as `schemaVersion` 2 (adds the optional ids and `iconItems`); version-1 files (before 2026-10-15) are read as they are and become version 2 on their next save; a future version falls back to a backup.
- **ForgetRecipes** (crafting job, Maintenance, local only): writes a backup of the whole character file next to it (`knowledge.json.forget-<UTC>`; nothing is removed when that fails), then removes every recipe and the stock with the recipe-screen coverages (`ability:craft.product`, `ability:craft.pairs`). Keep-stock rules, other stock and the icon items stay.
- **Writes are atomic:**
  1. write a temp file in the same folder and `Flush(true)`;
  2. parse and schema-validate it (`schemaVersion`);
  3. `File.Replace` with backups (5 generations), with bounded retries on sharing violations.
- **Reads** fall back to the newest valid backup. Only the host writes.

### 11.3 Planner (pure)
- **Inputs:** goals, keep-stock rules, recipes and stock.
- **Planning rules:**
  - keep-stock minimums are reserved and unavailable to goals;
  - recursion with cycle detection on (item, recipe-choice) paths;
  - shared intermediates are aggregated;
  - recipe choice: FewestCrafts, or Preferred, with deterministic tie-breaks;
  - batches are capped by `MaxBatchPerCraft` and by the UI maximum read before commit (split into several crafts);
  - success-rate recipes use a lower-bound yield;
  - time estimates are ranges from the learned is_block cadence (default 1.9-3.0 s per tick).
- **Not committable** when there is any of: a raw shortfall, UnknownStock, StaleStock, NeedsVerification (unverified or partial recipe), a cycle, or NoRecipe.
- **Re-plan** after every batch from resynced stock.

### 11.4 Features (plug-in contract)
- **What a feature is.** An `IFeature` in its own project that references Contracts only. It carries a manifest (`FeatureManifest`: id, version, `CoreApiVersion` == 1, requires, data files, switch, validated game build) and a `Register(IFeatureRegistrar)` that adds jobs, procedures and readers. Data files declared in the manifest are loaded by the host.
- **Isolation** (FR-02/03):
  - `Register` runs in try/catch on a worker with a 5 s budget. A throw, a timeout, an API mismatch, an untrusted data file or a duplicate name disables the feature; the reason is reported (tray and journal). The host keeps running.
  - Dependents of a disabled feature are disabled too (topological order).
  - A handler that throws fails its job as InternalError; 3 throws mark the feature Faulted and its jobs leave the whitelist.
  - Features never see IGameIo, ILogTracker or files (static scan); all game I/O goes through `IJobContext`.
  - A game build different from `ValidatedGameBuild` flips the feature's templates to provisional.
- **The job whitelist** is the union of the jobs of enabled features.

| Feature | Jobs (params) | Kind / switch | Now |
|---|---|---|---|
| core (Companion.Features) | Reset, OpenBagKey, OpenBagClick, ExpandMenu, CollapseMenu, OpenAbility, Calibrate | Navigation / Maintenance | Ready (templates cut from the live Calibrate of 2026-10-14). The menu stays expanded: only CollapseMenu collapses it |
| inventory | ScanBag | ReadOnlyScan; key-only (b, capture, read, Esc) | NeedsSurvey (`bag.grid`). Clicks inside the bag (tabs, cells) fall under the crafting gate and need nav-safe surveyed templates (SCAN-01). Tell the user that scans mark items as seen (TOS-01) |
| recipes | ScanRecipes | ReadOnlyScan | NeedsSurvey (`craft.pairs`, recipe list scrolling unproven) |
| crafting | Craft {item: ItemKey, qty: Int 1..999}, KeepStock {}, CraftLoop {slot: Int 0..10, count: Int 0..100000, batch: 1\|max, item: Int (optional product item id)}; ScanFavorites {}; ScanRecipeList {}; ForgetRecipes {confirm: yes} | Commit / crafting; ScanFavorites, ScanRecipeList: Navigation, no switch; ForgetRecipes: Maintenance, no switch, no input | Craft, KeepStock: NeedsSurvey (select recipe, set quantity, read requested quantity). CraftLoop: ready (live run 2026-10-14). ScanFavorites: ready offline (2026-10-14), ScanRecipeList: ready offline (2026-10-14, needs a live check of the drag scroll in a job); both press the item tips (2026-10-15). ForgetRecipes: ready (2026-10-15). See below |
| survey | SurveyScreen {fullFrame: Bool} | Survey; passive, NO input | Ready: identify the current screen, save template crops (or the full frame with the chat panel masked, if fullFrame), a filtered log excerpt (60 s) and a UI-state summary. Captures only screens the user opened (TOS-01) |
| homestead | HomesteadStatus, HomesteadCollect | ReadOnlyScan / Commit (homestead) | NeedsSurvey (`menu.homestead` template, `lumi.products` reader, collect button) |

**Craft flow** (for when the stubs are surveyed; CR-01..07, STK-01..03, PL-01):
1. Switch on.
2. No combat events in 10 s; scene approved; no match queued; no UI_REVIVE in 5 min.
3. Resync: Navigate to `ability` and read `craft.pairs`.
4. Plan; it must be committable.
5. Select the recipe, set the quantity, and read the requested quantity (journaled).
6. No user activity in the game since the job started (CR-07; gate 7 already enforces the 60 s cooldown).
7. Commit (Commit step).
8. Verify the start: is_block followed by the station UI within 1 s. An is_block without the station is HALT BlockedCraftSpot. Dismount lines are never used as a signal.
9. `AwaitCraftAction`: no input; post-craft popups are expected.
10. End state: the craft view re-shown OR the HUD (CR-04).
11. Derived delta = ticks × outputQty.
12. Ticks < requested means stopped early: no re-commit; re-read and re-plan. Two early stops are a HALT.

**KeepStock** is evaluated only on an explicit trigger or inside the user's session window, with the caps from `KeepStockConfig`. Daily counters use the server day (UTC+1).

**CraftLoop** (live run 2026-10-14; procedure `crafting.loop`, no planning, no reader). The switch is checked first (Refused, zero inputs). Each round: `OpenAbility` (Reset, ExpandMenu, menu.ability → UI_LIFESKILL_MAKE_VIEW); one Esc only if UI_LIFE_STAGE_PREVIEW is open (SkipIfSatisfied); slot 1..10: click `craft.fav.anchor` + the slot's offset (row-major, 5 per row; needs two rows of favourites, otherwise the anchor is not where expected and nothing is clicked); batch max: `craft.qty.max`; Make (`craft.make`, Commit step, expects the station show); `AwaitCraftAction` (no input; 1 or 999 ticks as the timeout bound). Items = the session's ticks. Ends when count is reached (count > 0), when a round makes nothing or no craft starts (`commit.noEffect`: materials out), or on stop / HALT / any other procedure end. A fixed 3 s pause separates rounds; every round is journaled (Stock record `craftLoop.round`), and a productive round marks all stock stale. A max batch can overshoot the count. The optional `item` (2026-10-15) is the product item id the caller expects in the slot (the tray sends its recipe's id): when the latest favourites recipe stored for that slot has another product item id, the job is Refused `slotItem.changed` with zero inputs; the id is journaled with every round.

**ScanFavorites** (2026-10-14; procedure `crafting.scanFavorites`, kind Navigation, gate none, no commit step). A read-only scan: no automation switch; slide clicks off, a craft in progress or Revive/Match busy → Refused with zero inputs. For slot 1..10, one run each: `open` (CallProcedure OpenAbility; from slot 2 on SkipIfSatisfied while UI_LIFESKILL_MAKE_VIEW is on top), `announce.close` (as in CraftLoop), the slot click (`craft.fav.anchor` + the same offsets as `crafting.loop`), `settle` (1 s wait), read `craft.product` and `craft.pairs`. Per slot (saved at once): a Recipe `<product key>|lifeskill|0` (output 1, station `lifeskill`, materials = {icon key, required}, `userVerified` false, `SourceCapture` = `screen:craft.fav/<slot>`, Complete or Partial; a Partial read never replaces a stored Complete recipe), StockObservations (CraftViewPair) for the product (`ability:craft.product`) and every material (`ability:craft.pairs`). Icon keys within 24 bits of a stored icon key (or icon item) reuse it. After the product's item tip (`crafting.productTip`: its logged name and id replace the OCR title), and unless the slot repeats the previous one, every material tile whose icon is not a known icon item is pressed once (the same procedure with the click moved to the tile's centre as measured by the tile-row reader, anchor-relative and inside the materials band; the 2 s pause before each press; journal `tip.material` with codes and the item id only): the logged item is learned, and the recipe and stock store the material under its name and id. A product named from the log replaces, instead of duplicating, a stored recipe with an OCR-only name (no product item id) of the same slot or card, or with the same materials (journal `replaced`). Stops at the first slot whose read repeats the previous recipe (an empty slot leaves it selected), shows nothing (blank panel, or the panel gone after slot 1), after slot 10, or on any other procedure end. A fixed 2 s pause separates slots; every slot is journaled (Stock record `scanFavorites.slot`). The Make view is left open.

**ScanRecipeList** (2026-10-14; procedure `crafting.scanList`, kind Navigation, gate none, no commit step; same refusals as ScanFavorites; at most 2 per hour). Reads every recipe of the Make tab's list (My Favorites, Recent, then the dish-type sections). Every input is placed from the list anchor `craft.list.anchor` (the "Make" page title at (485,123), fixed while the list scrolls; proof c14 shows it at the same place before and after the drag). Runs of the data procedure: `open` (OpenAbility; later runs SkipIfSatisfied, and a later run that had to open the view again ends the job Failed `list.viewReopened`), `announce.close`, then one of: `top` (DragAction from (420,420), +280 px), a card (`card` click with the card's offset, `settle` 1 s, read `craft.product` and `craft.pairs`), `scroll` (DragAction from (420,860), -280 px: two rows, less than the list's 518 px minus a row, so no row is ever skipped) + `scroll.settle` 0.8 s. The job: (1) opens the view; (2) drags to the top until a drag no longer moves the list (at most 12); (3) per round: captures, finds the whole cards (`RecipeList.FindCards`: 5 columns at x 364 + 113 c, tiles found by their outlines; rows found by any whole tile, a column has a card when its edges cover 40 % of the row), and selects every card whose list position (its y minus the measured scroll) was not read yet, row by row, only inside the list (20 px margin); stores it like ScanFavorites (`ScreenRecipes`, source `screen:craft.list/<n>`) unless its product was already read in this scan (Recent and My Favorites cards, skipped by name) or a stored recipe comes from a favourites slot (kept: the tray maps CraftLoop's slots from it; its stock is still observed); (4) drags up and measures the shift by comparing the list before and after (`RecipeList.MeasureShift`: row signatures, best shift clearly better than any 30+ px away; c14: -284 px for a 300 px drag); no movement = the end of the list (a drag cut short is retried twice, then `list.dragIncomplete`); an unmeasurable shift forgets the positions (names still prevent double records). Caps: 40 rounds, 300 cards (end "cap"). A fixed 2 s pause precedes every card and drag; every card (`scanList.card`), drag (`scanList.drag`) and the end are journaled. Item tips (product and unknown material icons) and the OCR-only replacement as in ScanFavorites; a list scan never replaces a favourites recipe. CLI: `Companion.Cli submit ScanRecipeList --wait --timeout 3600`.

**ForgetRecipes** (2026-10-15; kind Maintenance, no switch; at most 2 per hour). Local only: no procedure, no input, no capture. Required `confirm=yes`. The store writes `knowledge.json.forget-<UTC>` next to the character file first, then forgets every recipe and the stock with coverage `ability:craft.product` / `ability:craft.pairs` (11.2); the job ends Succeeded `recipes.forgotten` (data: recipes, observations, backup file name) or Failed `knowledge.backupFailed` with nothing removed. For one clean rescan after the OCR-title era: `Companion.Cli submit ForgetRecipes confirm=yes --wait`, then ScanFavorites and ScanRecipeList.

### 11.5 Automation switches
config.json `"automation": {"crafting": false, "homestead": false}` is off by default and changed only in the tray.
- The engine refuses, at the action level, any click on a Commit-tagged template or button, and any input while a Station UI is open, when the matching switch is off.
- Features cannot declare themselves read-only (GATE-01).
- Read-only scans are not gated.
- `input.slideClick` (default true; the user's decision of 2026-10-14) can turn all slide clicks off, leaving key-only procedures.

## 12. Privacy filter
- **Log text** (`ILogPrivacyFilter`, used by journals, diagnostics, survey excerpts and test fixtures) uses an allow-list of exact message patterns:
  - ui_mgr UI events;
  - StageMgr focus;
  - UpdateModule LostFocuse with only its `module:` and `screenPosition:` lines;
  - `[graphic]` resolution;
  - collect_mgr;
  - scene_load;
  - lifeskill_cook_mgr is_block;
  - jank_detector.

  Everything else is dropped: ema_vendor `baLogEvent upload` and any analytics (account hash, role_id, server, device info), chat (chat_mgr, chat_util, ejoy chat_helper), login and role dumps (`resp client.load_role`, guild_name, untimestamped dump lines), team/vendor lines with player ids, and mount lines. Role-id-like patterns `\b\d{4,5}-\d{4,6}\b` are scrubbed from anything kept.
- **Images** are persisted only through `IVision.EncodePng`:
  - template-sized crops;
  - the right HUD strip (Calibrate, user-initiated);
  - a full frame with the chat panel (660,590)-(1270,1009) blacked out, only in SurveyScreen with fullFrame=true.

  Full frames never go into diagnostics.
- **Journal** fields are codes only. Window titles are never read: cover windows are recorded by class.
- **Registry reads** are limited to `StaticRules.RegistryReadWhitelistPrefixes`; the key is never enumerated or dumped.
- **Tests.** A synthetic fixture (made-up chat, ids and analytics lines, no real data) must give zero hits after the filter. A journal schema lint rejects free text.

## 13. Offline test plan (MSTest, per unit)

| Unit | Tests |
|---|---|
| core-io | **StaticRulesTests** (all of section 1). Choke-point asserts (message, flags, hwnd, lParam, OpenProcess access, GetAsyncKeyState keys, key whitelist/deny). Slide geometry (c3 run 3 → (−838,327)). Cover rules on synthetic window tables (taskbar, topmost, tool, cloaked, game-owned, edge margin, 8-point probe, unstable). Monitor and exposure rules on a two-monitor test layout (left monitor at (−1920,0), primary at (0,0), 1920x1080 each). Home validation. Restore ladder decisions. Marker crash-recovery decisions. Pacing token bucket (fake clock). **Abort orders with the fake Win32 sink:** throw after DOWN → UP, 20 ms, slide back, unfocus; dead-man → SendNotify unfocus, then UP, then async slide; foreground → no unfocus; iconic → no SetWindowPos. KEYUP always follows KEYDOWN. Watchdog trigger table. Registry whitelist. No test touches a real window |
| logs | Byte-exact fixtures (allowed lines only) for every pattern in 6.1. Reads split at every byte boundary (partial entries, multibyte UTF-8, CRLF). 4-space separators. `\|` blocks (LostFocus before or after the block). Rotation where the new file is already larger than the old offset. Shrink. FileShare.Delete (renaming a temp log while polling succeeds). Advisory model (phantom opens, numeric/nil closes, `hide top ui nil`, scene clear). Busy brackets. Craft ticks (14 is_block → 13 ticks). Bootstrap finds the auto-collect status and login at the start of a large synthetic file. Login token hashed (no raw token in any output). Privacy filter zero-hit test |
| vision-ocr | On `proofs\out` PNGs (read-only), pasted into a 1920x1009 frame at x=1440: `hud.bag` found at (1748,312) ± 2 and `hud.toggle.collapsed` found. Provisional `menu.ability` and `hud.toggle.expanded` are not clickable. HUD classifier: c3b gives Collapsed; c1 (half scale, upscaled ×2) gives Expanded or Unknown, never Collapsed. Margin and mask behaviour. Zero-variance guard. Out-of-radius gives OutsideRadius. Region change. EncodePng blackout. Floors applied (threshold 0.5 in json becomes 0.85). Perception: OCR available (else Inconclusive), timeout gives Unknown, digit reader on synthetic glyphs, pair/abbreviated parsing, NeedsSurvey reader gives NeedsSurvey, identity mismatch gives ScreenMismatch, disagreement gives Unknown. TemplateCutter refuses non-full-scale sources for Verified |
| engine-nav | **Engine scenarios with fakes** (each asserts outcome, class, decision, input counts and journal): (1) cursor moved before the click: NotSent/AbortedAfterFocus, uncounted, back-off, focus-pair cap, patience → Park; (2) cursor moved in P4: Uncertain → settle → Ok, or RetryUncounted, with the 5th counted; (3) wrong UI with verified aim: Esc, suspect, one retry, second → HALT RecalibrateTemplate; unverified → uncounted; (4) nothing happened ×3 → step back → ×3 → reset+restart ×2 → HALT CountedFailuresExhausted within budgets; (5) PressNotRegistered → uncounted; (6) window incident restored → pause; third in an hour → HALT; restore failed → HALT; self-resize → HALT; (7) game foreground mid-step: UserActive, 60 s cooldown, Commit job Cancelled; (8) minimized: no SetWindowPos; (9) log late: LateSuccess, no second click; (10) template not found in 2 captures: PreconditionLost → step back/reset; far → LayoutChanged HALT; (11) toggle already in target: Ok with 0 clicks; toggle pressed but HUD unchanged → None; HUD Unknown → wait; (12) Forbidden after our click: exactly one Esc, then HALT; foreign Forbidden: wait, 0 Esc; (13) popup between locate and DOWN → Popup; (14) loading: 0 inputs until clear; (15) Esc opens a UI → HALT EscOpenedUi; (16) user UI_MAIL open: UserUiOpen park; with policy: Esc; (17) rotation mid-job → GameExited; (18) identity change → HALT; (19) focus failed once → retry, twice → HALT; (20) stop during wait → Stopped; pause/resume; (21) budget → HALT; (22) throttle → 60 s; (23) auto-collect 1/2/unknown → wait; status change during a step → HALT; (24) world press → HALT; (25) unknown UI → Esc + capture; second → HALT; (26) clock jump; (27) automation off → Refused with 0 inputs; (28) NeedsSurvey pre-flight → 0 inputs; (29) Calibrate with provisional X → PendingCompensation + one notification; (30) every journal record passes the schema lint. Journal and artifact store: caps, reparse refusal, privacy. **Navigator:** identify every seed screen (Confirmed, LogOnly, VisionOnly, Unknown); Unknown → Reset → identify; BFS shortest path; never via Forbidden or Commit; Provisional or NeedsSurvey hop → NeedsSurvey; BuildProcedure shape; data files (`ui-map.json`, `procedures.json`) deserialize with `ContractJson.Options` and cross-reference |
| knowledge-planner | Atomic write; corrupt file falls back to a backup; round trip with 0/1/n arrays and nested materials; schema version; derived-delta offset rules (before or after the observation, after LastAppliedOffset, rotation stale); stale marks; bindings and permissions; salted hash stable and no raw token on disk (scan the files). **Planner scenarios:** single recipe; 3-level recursion; shared intermediate; cycle through an alternate recipe; keep-stock reservation; unknown stock → UnknownStock shortfall, not committable; stale stock → not committable; success-rate lower bound; batch split; deterministic tie-break; unverified recipe → NeedsVerification; depth exceeded |
| features | Loader: a throwing Register disables only that feature; Register timeout; API mismatch; dependency cascade; duplicate job name; untrusted data file. Param validation: int bounds, enum, unknown ItemKey, extra or missing params, length. Feature static scan. Each NeedsSurvey job returns NeedsSurvey with zero inputs (fake runner counts). SurveyScreen sends no input. A Commit job with the switch off is Refused. Core jobs map to the 7 procedures |
| app-host | Pipe request validation (size, fields, id regex, ops). Queue limits (1+1, 10/h). Config defaults and atomic round trip. HALT latch persists. Trust list compare. Notifier de-duplication. Cli argument parsing and exit codes. Composition smoke: construct everything with fakes, no Start, no game access. Guardian argument parsing |

## 14. Templates and data available offline (vision-ocr writes `templates\`)
Positions are in game CLIENT coordinates. The builder must confirm every box by viewing the images (Read tool) before cutting, and record the source and crop in `TemplateDefinition.Source`.

| Template id | Source | Approx. box / centre | Status | Notes |
|---|---|---|---|---|
| `hud.bag` | `proofs\out\c3b_hud_right_x1440.png` (FULL scale, crop x offset 1440, collapsed HUD) | icon around crop (308,312) → client (1748,312); about 56x56 | **Verified** | Mask the red badge dot (top-right of the icon). Keep the "Bag" label only if static. Radius 24, threshold 0.90, margin 0.10. Tags Nav, HudAnchor. FireOn Down. Proven: c3 run 3 hit at (1748,314) |
| `hud.toggle.collapsed` | same image | four-petal icon around crop (412,290-300) → client (1852,290-300) | **Verified** | Mask the red badge dot. Tags Toggle, HudAnchor. Hud = Collapsed |
| `hud.toggle.expanded` | `proofs\out\c1_ability.png` (HALF scale, older layout, expanded menu) | X around half (930,160) → client ≈ (1860,320) | Provisional, SourceScale 2 | Classification only. Replace from Calibrate's `expanded.png` |
| `menu.ability` | `c1_ability.png` (half scale, older layout) | around half (865,222) → client ≈ (1730,444) | Provisional, SourceScale 2 | Stale: in the current layout (1732,444) is Homestead (c3 run 1). Never clickable. Replace from Calibrate |
| `menu.homestead` | none | — | (absent; needsSurvey) | Cut from Calibrate's `expanded.png` |
| `craft.make` | `proofs\out\live_ability_make_full.png` (full scale, Make tab) | rect (1380,839)-(1564,901), centre (1472,870) | **Verified** | Tags Commit, gate crafting, FireOn Up |
| `craft.qty.max` | same | rect (1233,837)-(1295,899), centre (1264,868) | **Verified** | Max-quantity button; FireOn Up |
| `craft.fav.anchor` | same | rect (537,740)-(645,773), centre (591,756) | **Verified** | "Recent" header, scrolls with the My Favorites grid; ScreenAnchor, radius 40; slot clicks = anchor + offset |
| `craft.materials.header` | same | rect (1143,627)-(1397,655), centre (1270,641) | **Verified** | "Materials Required" header; ScreenAnchor, never clicked; identity anchor of `craft.pairs` and `craft.product` (regions are relative to it) |
| `craft.list.anchor` | same | rect (424,99)-(546,147), centre (485,123) | **Verified** | (2026-10-14) the "Make" title of the recipe list page; ScreenAnchor, radius 16, FireOn Up, never clicked itself; anchor of ScanRecipeList's card clicks and drags (anchor + offset) |

- `data\readers.json`: the seed readers of 11.1 (all NeedsSurvey).
- `templates\templates.json`: the entries above. `hud.bag`, both toggles and `menu.ability` also carry the `NavFixed` tag (7.3 cross-monitor rule).
- Per-pixel masks (live 2026-10-14: the HUD icons sit on SEE-THROUGH discs; `menu.ability` cut over one scene scored 0.877 over another). `hud.bag`, both toggles and `menu.ability` carry `<id>.mask.png` (MaskFile; black = ignore) that keeps only the glyph, label and opaque outline: pixels stable (every channel within 10) across the full-scale right-HUD strips of the icon's HUD state (the bag: both states) and within 2 px of the stable bright glyph; badge rects stay masked; islands under 8 px dropped. `TemplateCutter masks` derives them into staging; a provenance test re-derives them from `proofs\out`. Thresholds unchanged (0.90 / 0.10).
- Digit fonts: `parchment-pair`, `badge-dark` and `counter-dark` are compiled into Companion.Perception (11.1); pair '2'/'8', badge '3 4 7 8 9' and counter '1 2 4 7 8 9' are still borrowed, and the "Requires" line has no capture. The bag fonts still need full-scale captures (Survey with fullFrame, or a dedicated capture).
- Staging: TemplateCutter writes only to `templates\staging\`. Promotion is a user/builder decision followed by a rebuild and "Trust this build".

## 15. Critique findings: adoption

**Adopted:** every finding not listed below, mapped as follows.
- F00-F31 and the safety, implementation and features lenses: sections 1, 6 and 7.
- Logs: 6.1-6.2.
- Privacy: 12.
- Host: 10.
- Knowledge, planner and features: 11.

PowerShell-specific findings keep their Win32 or logic lesson and drop the PS mechanics:
- PITFALL-16/17/18/21, KS-03 → one serializer (`ContractJson`), round-trip tests, a compiled-in non-injectable gate, no runtime code loading;
- PITFALL-12 → high-resolution timer + Stopwatch;
- PITFALL-14/15 → C# capture and NCC;
- OCR-02 → timeouts and a heartbeat timer;
- SEC-1 Add-Type/TEMP/profile → compiled build + trusted hashes;
- FR-02 module isolation → compiled features + try/catch/timeout + static scan.

**Rejected or adapted (with reason):**
- **STATIC-13 (GetAsyncKeyState ban): rejected.** F18 and PITFALL-08 need read-only mouse-button state to refuse a slide while the user is dragging. It is allowed only in GameIO, and only for VK 1, 2 and 4 (runtime assert).
- **STATIC-13 / WIN-3 / F01 / PITFALL-22 (SetConsoleCtrlHandler, QuickEdit, console handling): dropped as PS/console mechanics.** The host is a WinForms tray app with no console. Kept lessons: no blocking UI or I/O while displaced, `SessionEnding` triggers Stop, and the guardian covers host death.
- **PRESS-15 ("unlisted press-object name = WORLD_PRESS"): rejected.** Per F03 and PITFALL-10 the name is the object under the pointer at UNFOCUS (after the UI opened), not the pressed object. World press is decided from screenPosition = aim with an empty raycast, or from UI_LOCK_* / UI_NPC_TALK after our DOWN.
- **F09 ("foreign forbidden UI on top → Esc it"): adapted to UI-6.** A foreign Forbidden or Social UI may be the user's own purchase or invite, so the companion pauses and notifies without pressing Esc. Esc applies only when we opened it.
- **F09 ("refuse targets within a guard radius of shop entries"): adapted to CLK-5.** Bag is about 55 px from the timed-offer button, so a refusal radius would block the proven Bag click. Instead: refuse only inside the zone (+8 px), and require 1.5 s stillness with abort-on-movement within 120 px.
- **F24 ("restore any maximized-but-displaced window outside our step"): adapted to WIN-9.** Without a matching slide marker the deviation is not provably ours, so the companion notifies instead of moving the user's window.
- **F32 / SCOPE-22 (SlideClick off by default behind a trial flag, PERMISSION.md contradiction): adapted.** PERMISSION.md no longer exists, and the user's 2026-10-14 decision lists the c3 slide among the only allowed techniques. Kept: an explicit tray switch `input.slideClick` (default on, journaled with every HALT); key-only procedures work without it.
- **PITFALL-20 (lock file instead of mutex): adapted.** Both host instances are elevated, so a named mutex is fine for host-vs-host. The medium-integrity client discovers the host through the pipe (the actual lesson).
- **SEC-2 (STOP file creatable by anyone): not adopted.** It is redundant with the hotkey, the tray and the pipe `stop` op (any client may stop), and it would add a user-writable control file that the elevated host polls.
- **KS-01 ("refuse ALL jobs while the character is unknown"): adapted, then removed in 4.33.** Navigation, read-only, survey and maintenance jobs did not depend on the character; store writes, knowledge reads and Commit jobs required a known (and, for Commit, Allowed) character. 4.33 removed the binding and the permission at the user's request (one profile, 11.2).
- **PACE-7 (PRECONDITION_LOST ≤ 3 per job): adapted to F21's stricter ≤ 2 per procedure.**
- **F09/F11 detail ("taskbar click activates the game while slid"):** covered by the stricter cover rule (no Shell_TrayWnd cover at all) rather than a separate check.

## 16. Open questions and first live checks (not blocking the builders)
1. **Auto-collect status 2/3**: what do they mean? The current session logged 2 at login. Until the user confirms in the tray that "status 2 = not gathering", every input waits (AutoCollectNotIdle).
2. **Esc on a clean HUD with the menu expanded**: does it collapse the menu (F07)? Journal HUD state around every Reset; prove it during the first Calibrate.
3. **PrintWindow while slid**: does it block or return stale content (F08/CAP-16)? It is guarded by a 250 ms timeout; the first live click run records its duration.
4. **ClipCursor on fake focus (F27/CUR-19)**: a short reviewed proof run while the user's cursor rests on another monitor; the watchdog HALTs on any change regardless.
5. **Zero-exposure rule in practice**: if the user's windows rarely cover the slid window fully, slides will often be NotSent. Measure NotSent rates and ask the user before relaxing (e.g. allow exposure on the home monitor only).
6. **Code integrity**: should `build.ps1 -Install` (copy to Program Files, one elevation) be the default launch path?
7. **PERMISSION.md** was named as required reading but does not exist in the project any more; the spec relies on the 2026-10-14 decisions in EVIDENCE.md and the user's notes. Confirm nothing in it is still needed.
8. **Survey items needed for features**: full-scale captures of the expanded menu (Calibrate), the bag grid, the crafting view (pairs order, insufficient colour, quantity selector, Make button), Lumi's Hut, the station mini-UI, and whether Esc on UI_LIFE_ALCHEMIST_MAKE_POT cancels a craft (never use Esc there until surveyed).
