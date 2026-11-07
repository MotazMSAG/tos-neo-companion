# Companion roadmap

This is a ranked list of suggestions from the live exploration of 2026-10-15. Details for each item are in DISCOVERIES.md.

Ranking rule: first what the core goal needs ("pick a craft and a target, it handles the rest with minimal interaction"), then what the other items depend on, then value per effort, then risk.

| # | Item | Why (value) | Effort | Critique / risk |
|---|------|-------------|--------|-----------------|
| 1 | **Reader accuracy**: real digit glyphs (2, 6, 7, 8, 9, Requires/counter fonts) from the new full-size captures; material names from item-tip log lines | Many numbers read Unknown today, and every plan depends on correct counts | S–M | The captures exist now. Without this, Stock/Gathering/planning decisions run on gaps. Must come first. |
| 2 | **Knowledge cleanup + item ids**: purge OCR-garbage titles ("ams", "i pizza"), key items by game item id, rescan once | Clean names in the GUI; one identity for products and materials across modules | S | A small schema change. Do it with #1 so the rescan happens only once. |
| 3 | **Restore the idle rule** (`SystemIdleForSlide`), or a mouse-only stillness rule | You asked not to forget; clicking while you are mid-mouse-move is a real risk | XS | Trivial. Do it as soon as heavy testing ends. |
| 4 | **Stock module**: Stock tab grids (Ability / Homestead / Tailor / Hyperweather); names and ids learned once via tips, then counts only | The planner's input: what you have, without opening every recipe | M | Needs one more count font (white digits on icons) and grid scrolling (the drag piece exists). |
| 5 | **Craft planner / supply router**: "make X × N" → needed vs. stock → fetch missing (homestead → gathering → skip) → craft → repeat; tray shows what it waits for | This *is* your goal; it ties the modules together | M | **1.0 DONE 2026-10-16**: Craft = goal loop (plan → gather all short raw materials in one walk, up to 4 per trip, exact need − owned capped by the stock limit → craft sub-recipes first in typed batches, one Make per batch → Make-named missing items replanned); tray Start runs it for any recipe. Homestead supply (Lumi) still open. |
| 6 | **Gathering module**: Gathering Helper Finish, set targets with quantity, Begin; status from the log (`set_auto_collect_status` 1/2/0) | Refills raw materials (e.g. Water), which unblocks the most crafts | M | **DONE live 2026-10-15**: Begin teleports and gathers by itself (20 mushrooms ≈ 40 s); shared Kit module + Gather job; Craft is now the goal loop (plan → gather → craft → Make-named missing → replan). Helper gathering does not use the Limited Gathering Duration (user). |
| 7 | **Homestead via Lumi**: collect finished slots (default: only what the plan needs), queue needs (crops are free), never touch targets the companion did not queue | Some important recipes need homestead goods | M | Shared and first come, first served: always re-read, track our own requests, back off when slots are busy. Screens are simple and fixed. |
| 8 | **Scheduler and routines** (ROUTINES.md): periodic Lumi collect, gathering Finish/refill, "craft until out"; respect the user being active | Makes it truly hands-off | S–M | Only worth it once 4–7 exist. Keep the schedules coarse (hours, not minutes) to stay human-paced. |
| 9 | **Tray UX**: waiting reasons, stock view, module switches, a "what will it do" preview | Trust and control without reading journals | S–M | Grow it with each module rather than as a big redesign. |
| 10 | **Log catalogue module**: event ids (module + line + keyword), categories, privacy exclusions (chat, analytics, player ids, team payloads, telemetry) | Fewer captures; expectations become data | M | Build it incrementally, adding events as modules need them. A big-bang catalogue of 420 kinds has little value now. |
| 11 | **Navigation map + log-verified presses** for fixed buttons, HUD variants (field / town / homestead) and map tap-to-walk | Robust when captures fail (cross-monitor, off-screen) | M | Capture-based clicks plus the patch rule work well now (52/52), so the value is lower. Do it when a real failure needs it. |
| 12 | **Robustness backlog**: non-activating unminimize, window-size tolerance, unknown-popup learning (overlay vs. blocking), commit-job cooldown messages | Fewer stops on odd states | S each | Fix them as they appear live; most are rare. |
| 13 | **Daily chores** (Norton Town To-do, Donation, Kanu's Wish, Worship Catlord, Neighbor Sharing, Town Affairs) | Saves daily clicks | M | Off the crafting goal. Each chore may hide currency-spending confirms: map carefully. |
| 14 | **Out-of-game DM notifications** (your idea) | Never miss a whisper while away | S–M | You asked for it after the current goal. Needs one test whisper; show content transiently or not at all, never store it. |
| 15 | **Lumi assignment / production-chain optimisation** (Bakery, Sauce, Dairy, Juicer, Cake, Pizza...) | More homestead goods automatically | M–L | Assigning Lumis changes a setup that may be shared with another player: needs rules. Later. |
| — | **Manual crop plots** | — | L | Deferred by you: plot/crop/ripeness identification is its own project. |
| — | **Market / Run a Stall / Expand Inventory / Obtain Lumi** | — | — | **Not recommended**: currency and trading screens. Add their UI names to the forbidden list once known. |

## Next, chosen by the user (2026-10-20)

These come from the log study of 2026-10-20 (module counts over the last two days). The user picked them from a list of eight.

| # | Item | What the user gets | Built from |
|---|------|--------------------|------------|
| A | **"While you were away" summary** (built 2026-10-23, Release 3: notification + Activity card) | When the user comes back, one card and a pop-up, e.g. "Made 120 Salad, 3 trips, gathered 340, 2 pauses, gear worn out at 19:10". | The journal (craftLoop.round, plan.gather, plan.deferred, gear.notice, jobFinished, halts). |
| B | **Rule mode for food and homestead** (food part built 2026-10-23, Release 3: re-check after auto-eat; homestead supply needs a live survey of Lumi's Hut) | "Keep food at N": the auto-eat log lines (`drug_mgr [autoeat] supply eat_food`) count what is used and rule mode tops it up. A recipe short only of homestead goods (Peppercorn, Rosemary) gets a homestead collect instead of a stop. | AutoEatLogEvent, the Homestead jobs, KeepStockLoop, CraftPlanRun. |
| C | **Production stats page** (built 2026-10-23, Release 3: Stats tab, 7 days; map ranking needs map lines in the journal) | Crafts per hour, materials per gathering trip, best gathering maps, success and pause rates, as a chart page. | The journal plus stock history; map names from `map_mgr "table <map>"`. |

Not picked for now: AFK pop-up alerts (disconnect, safety stop, goal reached, whisper), the "where am I" status line, guild and honour war alerts, boss skill callouts, and a daily checklist.

## Cross-cutting suggestions
- **Safety list:** once their UI names are seen, add "Expand Inventory", "Obtain Lumi", shop and stall screens to the forbidden catalogue.
- **Privacy:** enforce the exclusions in code (journal + catalogue): chat, ema_vendor, `player_info_mgr`, `team/*` payloads, `monitor_mgr`.
- **Speed:** incremental rescans. Skip cards whose name/id and materials are already known; rescan only when the profession level rises. The full list currently takes about 8 min.
- **Test hygiene:** fixtures live in the tests' own folder (in progress); exploration uses capture-free click scripts.
- **Process:** a small direct fix + logging + a live test per problem; a single reviewer only for click-path or safety changes.
