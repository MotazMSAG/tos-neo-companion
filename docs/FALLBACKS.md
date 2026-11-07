# Fallback protocols

Motto: **1. Modularity. 2. Fallback protocols. Nothing stops us, nothing goes to waste.**

This file lists the general fallback rules and where each one lives in the code. It was written on 2026-10-16, after the
Make tab waited for an hour with the cursor resting on the other monitor and nothing tried another way.

## The general ladder (every press, every job)

Every action climbs the same ladder. Only the last two rungs end anything, and only a safety problem latches a HALT.

| Rung | What happens | Where |
|---|---|---|
| 1. Wait for a good moment | Act when the cursor rests (0.8 s still); abort and retry when the mouse moves during the action. Keyboard input is never blocked. | `GameIo.Readiness`, `ProcedureRunner.Actions` (`WaitClickReadiness`) |
| 2. Another press point | When the slide is refused (too far, monitor rule), try the alternative aim offsets around the target. | `ProcedureRunner.Actions` (`AimOffsets`) |
| 3. Another route to the same press | NavFixed HUD buttons may slide across monitors (fixed spot, no slid check needed). An off-screen anchor uses the press-patch rule. | `templates.json` tag `navFixed`, `SlidVerifyRules`, `PatchClick` |
| 4. Retry, then step back | A counted failure is retried, then the runner steps back to the latest earlier step whose precondition holds. | `ProcedureRunner.Loop` (`CountedLadder`, `DecideCounted`) |
| 5. Re-navigate | Reset (Esc to a clean HUD; Esc-safe popups are closed, a window Esc reveals is fine) and restart the procedure from its first step. | `ResetAndRestart`, `UiCatalog.EscSafe`, `reset.revealed` |
| 6. **Defer** (new) | The step cannot be done now and nothing unsafe happened: the procedure ends **Deferred** (no HALT). | `StepOutcome.Deferred`, `ProcedureStatus.Deferred` |
| 7. Do other work, come back | The job treats Deferred as "not now": it does what else it can, pauses (30 s doubling to 5 min) and plans again from the stock. | `CraftPlanRun` (`plan.deferred`), `Gathering` (`EngineDeferred`) |
| 8. Report | After 8 deferred rounds (about 25 min), the job ends with a plain report of what was made and what was deferred, and why. | `CraftPlanRun.Finish` (`deferred`), tray `CraftingModel` |

### When a step is Deferred

* **Unreachable for 45 s.** The press cannot reach the game from where the cursor rests: another monitor
  (`CrossMonitor`), no cover window, cover rejected, monitor rule, an exposed area, or a slide too far. This used to wait
  10 minutes and then park for up to 4 hours. Waiting on a busy user (cursor moving, a button held, game in the
  foreground) is not "unreachable": that still waits and retries, as the user asked.
  `HardRules.UnreachableLimit`, `ProcedureRunner.Unreachable`.
* **A recovery ladder runs out.** Retries, step-backs and restarts are used up, or the runner keeps cycling through the
  same state. The causes are `CountedFailuresExhausted`, `PreconditionLostExhausted` and `RepeatedState`. These used to
  latch a HALT. Now they end Deferred with the detail `exhausted.<code>`.
* **Backstop.** If a ladder runs out a 4th time within 30 minutes, it becomes the HALT: something keeps going wrong.
  `HardRules.SoftEndsBeforeHalt`, `SoftEndWindow`, `ProcedureRunner.SoftOrHalt`.
* **The user plays during a Make run.** If the game comes to the foreground or the user opens their own UI during a
  commit run, the run ends Deferred: `commit.userActive`, `commit.userInterference`, `commit.userActivity`. Make is still
  never pressed while the user plays. The goal job only pauses and comes back.
  * The commit gate's activity window starts at the later of two moments: the job's start, or the latest successful
    selection check. A selection check is a read-only procedure marked `verifiesSelection`, the product tip; the mark
    lasts until the next commit run ends.
  * CraftLoop reads the product tip before every Make, for every slot, whenever it knows the expected item (the
    caller's item, else the product stored for the favourite slot).
  * So after the user plays, a Make happens only once the recipe has been re-checked after they left the game. Without
    an expected item, the window is the whole job, as before.
  * The 2026-10-16 safety review found this flaw: a per-run window let a favourite slot click select a recipe the user
    had moved while away.
  * `ProcedureRunner.Gates`, `JobScope.VerifiedMono`, `ProcedureDefinition.VerifiesSelection`,
    `CraftLoop.VerifySelected`.

### What still halts at once

Safety problems still halt at once:

* a forbidden or purchase UI;
* Esc opening an unknown UI;
* a changed identity or integrity;
* a budget overrun;
* AutoCollect changing under our step;
* a template that must be recalibrated;
* an unknown UI seen twice;
* tampered files;
* internal errors.

These need a human, and no fallback goes past them.

## Fallbacks already in the modules

Perception and screen reading:

* A read made during an opening animation is retried after a settle (`Gathering.ListShown(settle)`).
* A slid capture that keeps disagreeing pauses for the cursor first. After a few pauses it counts as a lost
  precondition (`mismatch.pause`).
* A stray Esc-safe popup (reward, appraise "Improved", team recommendation) is closed with Esc. Then the step is retried
  (`popup.esc`, `EnsureHelper`, `CraftLoop.AfterBatch`).

Gathering module (`Companion.Features/Kit/Gathering.cs`):

* Walk passes:
  * The loose walk is followed by a strict pass when a learned target was not met. A zero-stock tile can show a
    mismatched picture.
  * A greyed tile means AtLimit; it is skipped and gathered on the trip after crafting.
  * A full queue means Deferred targets; the next run takes them.
* Quantities:
  * The amount is `need − owned` from the picker's exact count, capped by the "Stock Limit" room.
  * The quantity box default is not trusted as the room.
* The run: if the helper is not open, it is reopened. The Begin and Finish flags exempt the AutoCollect halt.

Crafting (`features/Companion.Feature.Crafting`):

* A batch is typed in one Make press. If the typing fails, Make is not pressed (`qty.notTyped`).
* Selection and the Make view:
  * The selection is verified by the product tip (`selectedItem.changed`).
  * A lost Make view is reported (`loop.viewLost`), and the plan finds the recipe again.
* The goal loop (`CraftPlanRun`):
  * A material that Make names as missing is recorded as 0, so the next round crafts or gathers it.
  * A favourite slot that moved falls back to a list find (`plan.slotMoved`).
  * A list walk cut by a reopened view walks again, up to 3 times (`plan.findAgain`).
  * A partly read recipe is re-read once from My Favorites (`plan.refresh`).
  * A material that cannot be gathered caps the goal instead of blocking it (`plan.capped`).

## Rules for new code (how to add a fallback)

1. **Bound every wait.** No loop waits on something the user's normal PC use can hold forever. Give it a short limit and
   a next rung.
2. **End soft.** "Cannot do it now" is `Deferred`, never a HALT. Keep HALT for safety, and for a soft end that keeps
   coming back (the backstop).
3. **Keep the reason.** The end detail (`unreachable.CrossMonitor`, `exhausted.PreconditionLostExhausted`) travels up as
   the job code `…deferred:<reason>`, so the tray can say why.
4. **The caller decides what is next.** A job that gets a Deferred part does its other work, pauses, re-plans from fresh
   stock and comes back. It stops only after a bounded number of tries, with a report.
5. **Nothing goes to waste.** Progress made before a deferral is counted and kept. A material learned along the way,
   such as a tile picture, a stock count or a recipe, is stored.
6. **Modular.** A fallback belongs in the shared module (engine, Kit) when more than one feature can use it. Features
   only decide what to do next.

## Forever mode and the overnight supervisor (2026-10-16)

**Forever mode.** Run the `Craft` job with `qty=0`. Each round plans `CraftPlanRun.ForeverChunk` (50, one game session; 100 needed more than a stock limit holds), gathers what is
missing, crafts, and plans again until stopped.

The old 30-round cap is gone. It was a runaway guard that ended a goal job even when a round did something useless, such
as gathering again and again without crafting. These specific guards replace it:

* **Gathering without crafting.** At most `MaxGatherOnlyRounds` (5) rounds in a row only gather. Then the round crafts
  anyway.
* **Rounds that make nothing.**
  * In forever mode, a round that made, gathered and learned nothing is paused and retried up to `MaxIdleRounds` (3)
    times. Then the job ends.
  * Deferred rounds follow `MaxDeferrals` (8).
  * Any progress resets all these budgets.
* **Out of a material that cannot be gathered.** Example: a homestead crop such as Peppercorn or Rosemary. `Feasible`
  caps the round to what the stock allows. At 0 the job ends with "limitedBy".

The gathering walk now stops at the first deferred press (`Gathering.IsStop` includes Deferred). The RepeatedState soft
end clears that procedure's cycle counts.

**Overnight supervisor.** `supervisor.ps1` (a separate script, not included) runs on its own and checks
`Companion.Cli.exe status` every minute:

* **No job running.** It submits `Craft item=<item> qty=0` again. After 3 quick ends in a row it waits 30 minutes.
* **A job is running but the journal is silent.** A gathering trip journals every minute and a craft round every few
  seconds. If the journal has not been written for 15 minutes, the job is stuck: the supervisor cancels it, and the
  next check submits it again.
* **A HALT.** It is never touched: a halt is a safety stop that a human checks.
* **The user takes over.** Tray Pause or Stop, or the file `supervisor.stop`, makes the supervisor exit.
* **Log.** `supervisor.log`.

## Open findings from the 2026-10-16 safety review

Fixed on 2026-10-16 (evening):

* The gathering walk stops at a Deferred press.
* RepeatedState clears its cycle counts.
* A Make that started before the round ended is counted (CraftLoop waits for the session).
* `UnreachableSince` resets on resume, pause, step back and restart.
* A deferred find in the slot-moved fallback, and a deferred favourites re-read, pause the goal loop instead of ending it.
* An engine-decided Cancelled (an unconfirmed press) pauses the goal loop; only the user's cancel stops it.

Still open below:

The review confirmed 19 findings. The two high-severity ones were the same flaw (the Make gate window, above) and are
fixed. The rest are open, ordered by severity:

**Medium**

* **Gathering walk after a Deferred press.**
  * `Walk.IsStop` and `StopOf` should treat Deferred as a stop. Then the walk ends at once instead of pressing on,
    paying 45 s per tile, and possibly ending in a HALT.
  * In `Probe`, a deferred tip run should set `SawDeferred` and close the picker (`set.close`).
  * Skip the strict pass when `SawDeferred`.
  * Do not infer "greyed" (AtLimit) or "notFound" for pending targets when `SawDeferred`. Otherwise CraftPlanRun
    excludes the target.
  * The standalone Gather job should return `gather.deferred:<detail>`.
* **RepeatedState soft end.** Clear the procedure's keys (prefix `<procedure id>/`) from `JobScope.Cycle`. Otherwise
  every retry in the same job ends at the first recurrence and fills the soft-end ledger.
* **Make delivered, then the user took over.** The run ends Deferred and the started session is not counted, so the
  goal may craft it again. In CraftLoop's non-success branch, check `CraftFlow.StartedAfter`, count the session's ticks
  and mark the stock stale.

**Low**

* **`UnreachableSince` goes stale.** Clear it on `ResumeIndex`, Park resume, `WaitWhilePaused`, `ResetAfterRestart`,
  `StepBack` and delivered attempts. Never clear it on busy-user reasons.
* **Park on unreachable reasons.** Park still waits up to 4 h. Add the same 45 s rule there, ending Deferred.
* **CraftPlanRun:**
  * The slot-moved Find fallback should record its deferral (`Defer` + continue).
  * Use a separate `deferralsExhausted` flag for "plan.deferred". Today RoundDeferred also covers a cap or a no-craft
    break.
  * No deferral pause on the last round (`&& round < MaxRounds`).
  * A deferred ScanFavorites refresh should reset `refreshed` and `Defer("refresh")`.
  * `Report.Deferred` should drop items that were later made, or gathering after a full walk.
* **Tray texts:**
  * Use a neutral "deferred" PlanNotes label; the current one blames the mouse even when the user was playing.
  * CoverGuidance should say that using the game pauses a goal job, not that it ends it.
* **Safety tests and live checks:**
  * Add an engine test for the `VerifiedMono` window: user activity before a verification lets the Make run; activity
    after it, or with no verification, defers it.
  * Live check: the Make sequence (tip, then Make) on a favourite slot and on slot 0.

## More fallbacks from the live tests (2026-10-17)

* **Queue check before Begin** (`Gathering.CheckQueue`, reader `crafting.gather.queue`). The game's accepted target per
  slot replaces the typed one: Scarlet Grape typed 9999, accepted 101, credited 101. A target cut to 1 is a full
  material: its "-" is pressed and it moves to AtLimit.
* **Gathering does not start twice in a row** (`plan.gatheringBlocked`). A broken gathering tool, durability 0, is the
  likely cause. The goal stops gathering for this job and crafts from stock; the report says to check the tool.
* **Journal written at once for job records** (`JobKit.Journal` flushes). Before, records waited for the next procedure
  end, so the file looked silent during trips and pauses.
* **Make list not recognised right after opening** (`ScanRecipeList.EnsureList`). Three more looks, 500 ms apart, before
  "unknown view".
* **Rule check held off** (`keepStock.deferred`). The game was in use: try again in 1 minute, not reported as "not
  found".

## Fallbacks added on 2026-10-17 (live tests)

* **A window the log does not know** (opened before a companion restart). The reset asks the screen: an Esc-safe window
  whose reader anchors are all on a capture gets one Esc; anything else is the user's to close (`reset.unlogged`,
  `reset.unloggedUnknown`). The log-only "clean" check had said clean while every click step found the HUD covered.
* **A target displaced right after a window opened** (`template.animating:<id>`). Within 2 s of a show event it is
  waited for, not halted as LayoutChanged. The Gather tab mid-slide caused the 2026-10-16 19:02 halt.
* **Rule mode stays on** (`Tray/RuleModeKeeper.cs`, flag file `%ProgramData%\TOS-companion\rulemode.on`). Every minute,
  when nothing runs or waits, the host is idle, the game is connected and the character is allowed, it starts
  KeepStockLoop. A rule job silent 20 min is cancelled; 3 quick ends wait 15 min. Live: it restarted rule mode about a
  minute after a companion restart. For rule mode it replaces `supervisor.ps1`.
* **A parked click step that stays unreachable** ends Deferred after 45 s, like a waiting one.
* **`UI_SPECIAL_BUFF_TIPS`** (a buff info popup) is Esc-safe; it had parked a job as "the user's window".
* **The Helper takes at most 200 per queued target**, even with more room. The queue check credits the accepted count.

## Favourites that moved or left (2026-10-18)

* **A blurry read of a known recipe** keeps the stored complete recipe but moves it to the slot where the scan saw it
  (`recipe.keptComplete` now updates the slot, time and logged id). Before this fix, Salad Savory, Clam Chowder and
  Raspberry Yogurt kept their old slots after the user reordered favourites.
* **A scan that reaches the end of the favourites** (`repeat`, an empty slot, or slot 10) releases every recipe that still
  claims a slot it was not seen in. The source becomes `screen:craft.fav.old/<slot>`, the result field is `recipesReleased`,
  and the craft job finds the recipe in the Make list. A slot the scan could not name keeps its claim. A scan that ends
  early releases nothing.
* **An outdated favourite in the window** can still be started with a count: the Craft job tries the old slot, sees
  another product (`slotItem.changed`) and finds the card in the list (`plan.slotMoved`). Only "until materials run out"
  still needs a confirmed slot. Before this fix the window greyed out Start, and the user read that as "uncraftable".
* **`UI_TIPS2_OVERLAY`** (the skill screen's tip layer: `open const ui`, never a close line) matched the Forbidden
  prefix `UI_TIPS2_` and parked a Craft job for good. It is now exempt from the prefix (`UiCatalog.ForbiddenPrefixExempt`)
  and seeded as an Overlay; the exact list and the substrings still block the exchange tip. Open: a general rule for any
  Forbidden name that opens and never logs a close (it blocks until the game restarts).
* **The gear durability Notice** ("<gear> Durability is insufficient ... pause and change Gear?", Ignore / Replace) is
  not logged and comes back on every gathering tick. `Gathering.DismissGearNotice` (readers `crafting.gear.notice`,
  `.helper`; procedure `crafting.gearNotice`) ticks "Don't show again today" and presses Ignore. It runs in the
  gathering wait (every 30 s), before Finish/Begin and before every craft step. The user's message is "gear.notice"
  (tray, red status line, Activity). Replace is never pressed. Live fixes the same day:
  - the reader's log top is "*" (any): the notice is unlogged and sits above any screen, so a log-top check skipped
    every look (26 checks, no hit; it found the notice at once with "*");
  - the two clicks carry the new step flag `duringAutoCollect` (plain non-commit clicks only): the engine blocks every
    input while the Helper gathers, and the notice shows only then;
  - `UiCatalog.NeverLearnedStack` (UI_TIPS2, UI_TIPS2_OVERLAY): an Esc while the max-stacks tip showed logged
    'hide top ui UI_TIPS2', the tip was learned as a Stack, and its show on every tick tripped the popup gate of the
    box click (parked an hour).
* **A stale fake unfocus** (the game in front, Unity's last focus line LostFocus): Unity ignores the user's UI clicks
  until a minimise/restore. `GameIo.ResyncFocus` (host log pump, every 0.5 s) sends the focus-on sequence after 1 s of
  mismatch, at most every 5 s, never during an input.
* **Open (2026-10-18): a server disconnect.** At 19:09 the game lost the server (error 11001, then "hall.login failed")
  and showed "Unable to connect to the server" with Return to Login (in the `UI_TIPS2_OVERLAY` notice layer, which
  logs only its first open). The reset's Esc cannot close it: 12 Esc presses latched BudgetExceeded. Wanted: detect
  the disconnect from the log (`connect_host err`, `[login:error] abort`, `ping_timeout request re_login`) and end the
  job as "game disconnected" with a clear message, without pressing Esc.

## Rule mode (keep-stock, 2026-10-16)

`KeepStockLoop` (`features/Companion.Feature.Crafting/KeepStockLoop.cs`) works rule by rule:

1. It selects the recipe in the Make list (ScanRecipeList find mode).
2. It reads the owned badge under the product (`craft.product` / `product.owned`).
3. It makes `keep at − owned` through the goal loop. A rule that cannot be read is skipped and journaled.

After a pass that made nothing, it waits 10 minutes and checks again, until rule mode is turned off. The main window's
Rules tab edits the rules (knowledge `SetKeepStockRules`) and turns rule mode on and off (submit or cancel).

## Two false stops fixed (2026-10-21)

* **`WorldPress` from an NPC speech bubble.** A drag on the Gathering Helper's list met `UI_NPC_TALK`, the bubble
  nearby NPCs show by themselves (405 times in one day), and latched a HALT although the game's raycast named
  `Image_Bg1` (a UI object). `UI_NPC_TALK` is no longer a world-press indicator, and a raycast that names a UI object
  at our aim overrules the indicators. An empty raycast at the aim still halts as a world press.
* **Favourite slot clicks cancelled again and again (`SlidTemplateMismatch`, 75 in one day).** With the cursor low
  on the monitor, the slide pushed the favourites anchor off the drawn screen, and commit procedures never used the
  off-screen anchor patch rule. The new step flag `selectionVerified` (plain non-commit clicks only) lets crafting.loop's
  `slot.*` clicks use it. CraftLoop reads the selected product's tip before every Make, so a wrong selection never
  reaches Make. The Make press keeps the strict check.

## Gathering walk: no more "not seen = at limit" (2026-10-22)

* **The bug.** After walking the Gathering Helper's list, a learned material the walk had not met was taken as greyed
  at its stock limit. A walk cut short (a page read finding no tiles counted as "the end of the list"; failed or partial
  drags) left Scarlet Grape, in colour with 4 owned, unmet; it became "at limit" and capped the goal. The job had pressed
  no grape tile at all.
* **Now.** Unmet learned materials are `gather.notSeen` (with `walk` complete or incomplete), tried again on the next
  trip, never at limit. "At limit" comes only from a recognised greyed tile (`reason` fadedTile, with page, tile and
  distance) or a picker with no room (`reason` pickerNoRoom, with owned and limit). A page read that finds no tile is
  retried twice and then ends the walk as incomplete (`pageUnreadable`), never as the end of the list.
* **Diagnostics.** Every page: `gather.page` (read status, tries, tiles, greyed, new, recognised item ids). Every walk
  end: `gather.walkEnd` (allQueued, queueFull, endOfList, pageUnreadable, scrollFailed, maxPages). The scroll to the top:
  `gather.top` (drags, empty reads). When a wanted material was not seen, a screenshot of the Helper goes to the
  diagnostics folder (`gather.snapshot`; capture scope `gatherHelper`, only while the Helper is the top UI, which covers
  the chat). The Activity tab shows not-seen and at-limit lines with their reason.

## Stock from the Make tab, and sub-recipe caps (2026-10-23)

* **Fresh counts before every planning round** (`MakeTabStock`, reusing the favourites scan's slot run and the recipe
  panel reader). The goal recipe and every sub-recipe below it that is a favourite are selected, and the owned counts of
  the product and of each material shown are folded into the store. Nothing is scrolled. Each slot is journaled as
  `stock.makeTab` (slot, code, observed, unknown). Before this, a day-old "0 Thick Fish Soup" made a Scallion Fish Cake
  goal gather Water while 157 soups were in the bag.
* **The goal cap looks into sub-recipes.** A sub-product whose raw material cannot be gathered in this job (not in the
  Helper, or named missing by Make with no supply) limits the goal to what the bag's sub-products plus that material
  allow (`SubRecipeCap`). Example: Thick Fish Soup needs Rosemary, a homestead good.
* **Esc ignores a keyboard layout change** (`GameIo.Key.cs`). It is the same key in every layout. Only B keeps the
  layout guard. Before this, a switched input language refused the Helper tip's Esc 1,084 times in 18 minutes.

## When the companion itself dies (2026-10-23)

* **What happened.** On 2026-10-23 at 00:20:43 local the host vanished mid-job. The user had paused, resumed, stopped
  and allowed it again while the gear notice was being dismissed. There was no journal line, no Windows crash event, no
  dump and, for the first time, no `guardian.recover` record, so the cause is unknown.
* **Now.** `CrashLog` writes `diagnostics/<utc>-crash-<kind>/crash.txt` for unhandled exceptions on any thread, the UI
  thread (reported without ending the app) and unobserved tasks, plus a guardian crash handler. The guardian writes
  `diagnostics/<utc>-host-exit/exit.txt` with the host's exit code whenever it is not 0, and adds `hostExitCode` to
  `guardian.recover`. The exit code distinguishes a stack overflow (0xC00000FD), an unhandled .NET exception
  (0xE0434352), an access violation (0xC0000005) and End task (1). If neither file appears after a death, both
  processes were ended from outside.

