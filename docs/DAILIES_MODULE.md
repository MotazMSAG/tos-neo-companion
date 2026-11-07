# Daily checklist module design ("Today" tab)

**Goal:** one list of the user's in-game dailies that ticks itself from the game log, plus a few events and the companion's own
work. It only reads: no clicks, no screenshots, nothing sent to the game. One list for the whole account (never per character).

**Asked:** 2026-11-04 ("I want it to auto-update"; "remember UX UI and design, not just patchwork").
**Design canvas:** a private mock-up (not included) with four boards: the Today tab (clickable, with a
scenario switch for evening / game closed / new day), Edit my list, Tray and reminders with row states, and How it connects.
**Status:** built (see "Phases" below).

## What the game log shows (scans of 2026-11-04: 4 play days, plus a live capture of Kanu's Wish)

- **The log has no "success" lines.** A daily is seen through four kinds of trace:
  - its screen plus a reward pop-up while that screen is the newest open one (`UI_ITEM_GET_TIP`, `UI_ITEM_REWARD`,
    `UI_REWARD_FALL`, settle screens);
  - a server refusal that means "done", e.g. `errcode from: arena.arena_sweep 2659` (no tries left),
    `town.new_active_bless 3630`, `home_month.month_get_daily_reward 3597`;
  - a server push: `convoy_mgr.notify_convoy_commit_task` (3 a day), `honor_war_network.notify_honor_war_stage is_win`,
    `guild_boat_mgr.notify_guild_finish`, `weather_solo_pve_mgr.notify_reward_info`;
  - a quest step or a map:
    - `task_mgr action state: 2, chain:C, step:S, task_id:T` and `realy commit:T`;
    - `cave_mgr scene_enter <id>` plus `[scene_lod] load_scene done <name>`, e.g. nvwushen_* is the Goddess Tower and
      *_husong is an escort.
- **Kanu's Wish (3 a day), from the live capture:**
  - It happens in Norton Town (map `town`, 40302).
  - Each hand-in is a reward pop-up (`popup_seq add_show_seq UI_ITEM_REWARD n`). Within 1 s the client sends a town
    refresh burst (`request too frequency : town.task_info` ... `town.get_cat_factory_list` x5).
  - The pop-up comes within 30 s after a Kanu quest commit (`1100140xx`, chain 7069x).
  - Shop purchases and building upgrades in town also trigger the refresh, but they follow no Kanu commit.
  - The rule counts 3 on Monday and 2 on Wednesday. Wednesday's third hand-in left no trace in the PC log.
  - Session 1 found that Wednesday's 03:48:45 talk-then-reward was **Worship**, not Kanu: the worship screen had
    reopened at 03:48:39, and the refresh burst included `town.new_worship_get_chest`. So talking alone is not a Kanu
    trace.
- **Resets (clock times at UTC+3, the data file's `timesUtcOffset`; the companion moves every time to the PC's time
  zone, and to the daily update it sees in the log, by itself):**
  - the daily update at 07:00 (`server_push.daily_update`; the client then refetches activity center, guild forge and
    donate, goddess tower, battle pass, EXP limit and town energy);
  - the calendar day at 02:00 (`notify.on_new_day`; mostly event switches). **Worship follows it:** on 11-03 a bless
    at 03:37 worked, and the 17:23 tries were refused; on 11-04 the 03:48 worship is that calendar day's;
  - weekly, Monday 07:00 (`notify.weekly_update`; Honor War, guild boss cycle, weekly maze).
  - An evening session often runs past midnight, so one evening counts as one checklist day (it ends at the 07:00 reset).
- **Event timetable:** the server announces every switch:
  `gameplay_network [notify_change_feature] instance: <gid> <name> <0|1>` (a burst at login is a snapshot, not a switch).
  - Seen: guild bear boss 21:44–21:59, guild spa 21:59–22:19, guild battle 22:19–22:54, season PvP 22:59–00:59,
    world boss 11:59–02:24, town party games 11:59–03:59, guild boat on Friday around 22:00–22:44.
- **Not in the log:** red-dot contents (the `get_red_dot` reply is empty), anything played on the phone, and anything done
  with no reward pop-up and no server line.
- **The game keeps only its last few launches.** Saturday 10-31 was already gone. So the checklist records live and keeps
  its own history.

## UX

It follows MainForm's 2026-10-16 rules: status always visible, one primary action per screen, recognition over recall,
progressive disclosure, one font (Segoe UI), the 8 px grid, the same colours, and nothing enabled that cannot work.

- **Placement (approved by the user, 2026-11-04):** the window becomes the checklist's home, with four tabs.
  - **Today:** first, and opened by default from the window and the tray. Its title carries the count ("Today · 10 left").
  - **Tools:** one card per tool: Crafting and gathering, Kitty Tea House, and later the homestead chores.
    - Each card shows a live one-line status and an Open button that opens the tool in its own window.
    - Crafting keeps its Craft, Queue and Rules pages, in the crafting window.
    - The Tea House helper keeps its own window.
    - Feature modules register their own cards.
  - **History:** Activity and Stats on one page: what the companion did, plus the 7-day charts, dailies included.
  - **Settings.**
  - The status card (Pause/Stop) sits at the top of every window. The tray menu and Ctrl+Alt+Pause are unchanged.
- **The list (left column):**
  - Header: "10 left" (19 px bold), "14 of 24 done today", a green progress bar, "Resets at 07:00 · in 9 h 13 min" and a
    secondary "Edit list" button. The tab has no blue primary button: it is a view.
  - Remaining items come first, grouped in collapsible sections: Every day, Homestead and Norton Town, Events and season,
    The companion. Each section header shows "N left of M"; sections with nothing left are hidden.
  - Done items collapse into one "Done today (14)" group at the bottom.
  - Rows are 52 px:
    - a 44 px check target;
    - the name;
    - a detail line: "2 of 3 · last at 21:40", "20:34 · seen in the game", "Ticked by you", "You usually do it around
      21:20" (learned from history);
    - a count chip for items with several per day.
- **Row states** (shape and fill, not colour alone):
  - to do: a hollow circle;
  - in progress: a blue ring filled to the count;
  - done: a green disc with a white check;
  - ticked by you: the same disc, with "Ticked by you";
  - marked not done by you: hollow, with "You marked it not done";
  - learning: an amber "learning" chip while the reset time is unconfirmed.
- **Interactions:**
  - A click on the circle ticks or unticks. The user's choice wins until the reset; ticking an auto-done item back to its
    automatic value clears the override.
  - Undo is the same click.
  - A tooltip says how the item is detected.
  - Keyboard: Up/Down move, Space ticks.
- **Side column (cards on #F2F5FA):**
  - **Coming up today:** open now (green dot, time left), then upcoming (blue "In 12 min"). A bell per event gives a
    heads-up 5 minutes before. Times come live from the server's switches; when the game is closed they read
    "Usually …".
  - **This week:** Honor War rounds; "Weekly reset: Monday 07:00".
  - **Last 7 days:** small bars, today in blue; days with no record are dashed.
  - **Reminder:** "Remind me at [01:30] if dailies are left", a quiet notification that never takes focus.
- **Edit my list (dialog):**
  - every known daily, grouped, with a search box;
  - a checkbox to show each one, an editable name, and a "how it's seen" line;
  - "3 a day" chips;
  - "Add your own" (hand-tick items);
  - Save (primary) and Cancel.
  - This is how the user approves items. Nothing is hidden from them.
- **Glance surfaces:**
  - tray tooltip "Dailies: 10 left · Guild Spa in 12 min";
  - tray menu item "Today: 10 dailies left", which opens the window on Today;
  - optional reminder and event toasts;
  - later and optional: a small non-activating list over the game.
- **Edge states:**
  - game closed: the status card says so, ticks are kept, and the clock still resets;
  - new day: a banner "New game day. Yesterday you finished 21 of 24; missed …";
  - first run: "Learning your dailies: play normally, items tick by themselves";
  - start-up catch-up: "Reading today's log…" until it is done.
- **Narrow window:** two columns at 840 px of client width or more, otherwise one column with the cards below. The minimum
  window stays 760x540.

## Architecture (reuse first, data first)

- **Log events:** extend Companion.Logs, the same LogTracker that PumpLog already polls every 500 ms. New records go in
  `Contracts/Logs/LogModel.cs`, with patterns in `LogPatterns` and parsing in `LogEntryParser`:
  - `ServerReplyLogEvent(Request, Code)`;
  - `ThrottleLogEvent` gains `Request`;
  - `ServerPushLogEvent(Module, Name, Win?)`, allowlisted names only;
  - `QuestLogEvent(Chain, Step, TaskId, Committed)`;
  - `MapEnteredLogEvent(SceneId, Name)`;
  - `FeatureSwitchLogEvent(GameplayId, Instance, Open, Snapshot)`;
  - `ResetLogEvent(Kind)`;
  - `RewardPopupLogEvent(Seq)`.
  - NPC talk is the existing `UiLogEvent` for `UI_NPC_TALK`.
  - Only names, ids, codes and booleans leave the parser. LogPrivacyFilter still drops chat, players and team lines first.
- **New module `src/Companion.Dailies`** (no WinForms):
  - `DailyCatalog`: loads and validates `features/Dailies/data/dailies.json`. It is part of the trusted build, like other
    feature data.
  - `DailyTracker`: a pure rule engine. Its inputs are log events, finished companion jobs, the clock and user ticks. It
    keeps the context: the newest open screen (180 s window), the last NPC talk, the last quest commit, the current map
    and the town refresh. It applies counts, caps and per-item resets, and builds the timetable from the switches.
  - `DailyStore`: `%ProgramData%\TOS-companion\dailies\`:
    - `today.json`: atomic, written at most once a second;
    - `history.jsonl`: one line per finished day;
    - `prefs.json`: shown items, names, reminder, bells.
    - The host is the only writer (runtime ACL).
- **Rules, one entry per item, any-of detections. Example:**
  `{"id":"arena","name":"Arena","section":"day","reset":"daily","detect":[{"reward":{"screen":"UI_ARENA_VIEW"}},`
  `{"refused":{"request":"arena.arena_sweep","code":2659},"means":"allUsed"}]}`.
  - Other detection kinds: `push` (with a count), `quest` (task-id pattern), `npcTalk`, `map`, `townRefresh`,
    `notAfterScreen`, `switch` (events) and `job` (companion work).
  - No code per daily.
- **Host wiring:**
  - `HostRuntime.PumpLog` passes the events it already reads to `Dailies.Feed`;
  - JobHost's finished jobs go to `Dailies.OnJob`;
  - the host's 1 s tick calls `Dailies.Clock(now)` for the reset fallback, the reminder and event heads-ups (through the
    existing Notifier);
  - at start and after a log rotation, today's part of log.txt (and .bak/.bak1 when they cover today) is replayed on a
    background thread. The replay is idempotent, and hand ticks are merged back in.
- **UI files** (MainForm only adds the tab):
  - `Tray/Today/TodayPage.cs`;
  - `TodayViewModel.cs`: pure, unit-tested, like CraftingViewModel;
  - `ChecklistView.cs`: owner-drawn and double-buffered, with accessible names;
  - `EventsPanel.cs`, `HistoryStrip.cs`, `EditListDialog.cs`.
  - Refresh happens on the snapshot version in the existing 1 s Tick, with no cross-thread UI calls.
  - Tray: StatusLine adds "· N dailies left", and the new menu item calls `OpenMain` + `SelectTab("Today")`.
- **CLI:** `Companion.Cli dailies` prints today's list from `today.json`, for checking live runs.

## Privacy and safety

Read-only end to end:
- no input, capture or game-window calls;
- no network;
- no raw log lines stored (only item states, times and counts).

It touches no safety path (input timeline, watchdog, commit gates), so it needs no safety review.

## Tests

- **Parser:** one test per new event, using masked real lines.
- **Tracker:**
  - Kanu: Monday 3, Wednesday 3; shop purchases and building upgrades are excluded.
  - Arena 2659 means all tries are used; escort pushes count up to 3.
  - Resets: 07:00, 02:00 and Monday, plus the clock fallback when the game is off.
  - A hand tick wins until the reset.
  - Replay twice gives the same state.
  - A rotation in the middle of the day.
  - Event switches give open/closed state and learned times.
- **ViewModel:** order, texts and the tab label.
- **Store:** atomic writes; a damaged file falls back to the backup or to empty, and is reported.

## Phases

All of this, including the optional parts, was approved by the user on 2026-11-04. The plan is one step at a time.
1. **Brain: done 2026-11-04.**
   - **New log events** in Contracts/Logs: `ServerReplyLogEvent`, `ServerPushLogEvent`, `QuestLogEvent`,
     `MapEnteredLogEvent`, `FeatureSwitchLogEvent` and `ResetLogEvent`, plus `ThrottleLogEvent.Request`. Patterns and
     parsing are in Companion.Logs.
   - **`LogReplay.Read`** re-reads a whole log file from a given moment.
   - **`src/Companion.Dailies`:**
     - `DailyCatalog` (parse and validate);
     - `DailyTracker` (rules, per-item days, user ticks, saved state with a watermark);
     - `DailyService` (thread-safe; buffers live events during the start-up replay).
   - **Data:** `data/dailies.json`.
   - **Tests:** Companion.Dailies.Tests (16) and Logs DailyLinesTests (3).
   - **Checked against the real log** with a throwaway tester. It read 28,000 events since Monday 07:00 in 0.5 s, and
     Monday's and Tuesday's lists match the scans.
   - **Two rules were fixed from that check:**
     - only the close of an open screen counts as "a shop just closed" (a late dispose had hidden a Kanu hand-in);
     - Kanu needs a quest commit, not just a talk.
   - **The CLI moved to step 2:** it reads the host's saved state.
2. **Home window: installed 2026-11-04 as Release 4.34.**
   - the Today page: list, progress, reset countdown, hand ticks, Done group, Edit my list;
   - the tray line and menu item.
   - Then install when idle, and the learning day.
3. **Tools:**
   - the Tools page, the crafting window and the History page;
   - the learning-day fixes in one batch.
4. **Extras:**
   - Coming up from the live switches, with bells and the reminder toast;
   - history bars and the Stats chart, plus the "you usually do it around …" hints;
   - the mini list over the game.
5. **Later, on its own:** read the game's red dots and daily panel from the screen.

## Open questions for the learning day

- Does Kanu's Wish reset at 02:00 or 07:00? Does Worship?
- What are the guild-window rewards (check-in or orders)?
- Are the top-up and gacha rewards free dailies?
- What are the NPC daily quests called (the log only has chain ids)?
- Which events does the user want bells for?

## Per-daily detection rules

The detection rule for each daily (which log lines tick it, and how they are counted) lives as data in
`data/dailies.json`, checked by the tests in `tests/Companion.Dailies.Tests`.
