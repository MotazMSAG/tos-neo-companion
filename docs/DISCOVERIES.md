# Game discoveries: material for new modules

These were explored live on 2026-10-15, with the companion paused, using single slide clicks, Esc and captures. Coordinates are client pixels (1920x1009). "Log" means the game log `<game folder>\Log\log.txt`.

## Item identity from the log (shared, all modules)
- Every item tip writes `item/item_tips_mgr.lua:200 ... print name, uuid, id : <Name> <ItemId>`. Examples: White Toast 1051000, Water 20330037, Psychic Vug 10102013.
- Tips open from:
  - the product icon on the Make tab (1130,375);
  - a material tile;
  - a Stock grid item;
  - Make itself, when a material is missing.
- The tip screen is `UI_ITEM_TIPS_PASS_TOUCH`. Esc closes it. A press on its mask is logged as `Image_zhezhao`.
- Status: being built into the crafting scans (names from the log replace OCR titles).

## Crafting (verified)
- The cooking station is `UI_LIFE_ALCHEMIST_MAKE_POT`.
- A successful craft logs, in order: `lifeskill_cook_mgr is_block`, the pot shows, `UI_ITEM_GET_TIP` (about 1.3 s later), the pot closes.
- Materials missing: Make opens the missing item's tip, and its name and id are logged.
- `slot=0` means the currently selected recipe.

## Stock tab, Ability screen (candidate: Stock module)
- Open: the right-hand tab at (1738,578). It switches tabs inside the screen and logs no UI event; the pressed object is logged as `Label_Text`.
- Sub-tabs: **Ability** (190,226)\*, **Homestead** (610,226), **Tailor**, **Hyperweather**. (\*From the half-scale shot: Ability at about (380,226).)
- Ability stock is a grid of material icons by section (Crystal, Essence, ...), with a count at the bottom right of each icon. Faded icons with no count mean 0. The grid scrolls.
- Homestead stock holds processed goods (flour, buns, cookies, cream, butter, jams, ice creams, ...). Its footer shows:
  - storage used/limit, e.g. 914/1270;
  - wood 221, a second material 229, nails 256;
  - "Expand Inventory", which likely spends currency: **never press**;
  - "Treebond Warehouse".
- Pressing an item logs its name and id (see above).
- **Module idea:**
  - First pass: press each icon once to learn its name and id from the log, keyed by icon fingerprint.
  - Later passes: read only the counts, a different white digit font.
  - Result: full stock knowledge for planning, instead of per-recipe owned counts.

## Gather tab and Gathering Helper (candidate: Gathering module)
- **Gather tab** (1736,716):
  - regions Archi Isle, Sage Forest, Winterhill, Tearwoods, Mutley Port, ... with gathering spots and abundance ("Plenty", "A lot");
  - an arrow button that probably travels to the spot. **Not pressed**: it would move the character.
- **Gathering Helper** (cat, left, about (156,330)) opens `UI_LIFESKILL_AUTO_COLLECT`:
  - a 4-slot target queue: each slot shows progress (e.g. 1/1), and a done queue shows "Complete" and a **Finish** button (about (1490,300));
  - an empty queue shows "Set targets" and **Begin**;
  - "My Favorites" materials with stock counts; greyed ones are probably at their stock limit;
  - Tool Strategy: use the highest-level tool / the tool matching the gathering spot / no tool;
  - Tool Bag;
  - "Offline mode activates upon logoff, continuing to gather any unfinished materials".
- Pressing Finish claimed the completed queue and logged `lifeskill_auto_collect_mgr ... set_auto_collect_status 0`. The pressed object was `Image_yh230522a2`.
- Tapping a material opens `UI_LIFESKILL_AUTO_COLLECT_SET`: name, description, stock limit (e.g. Sea Crystal 157/300), target quantity (−, +, max; default 60) and Confirm.
- Esc closes the target picker, then the helper.
- **Module idea, closing the loop:**
  1. CraftLoop ends with "material X missing" (from the log).
  2. The Gathering module opens the Helper, presses Finish if the queue is complete, and queues X (target = need − stock, within the stock limit).
  3. It presses Begin and watches `set_auto_collect_status`.
  4. When gathering completes, it presses Finish and CraftLoop resumes.
- **Open questions, need your OK to test:**
  - Does Begin move the character or gather in place?
  - How long does a target take?
  - What do the status values mean (0 = idle after Finish; 2 was seen earlier while idle)?

## Homestead and farming (explored 2026-10-15; candidate: Homestead module)
- **Getting there:**
  - HUD menu (expanded) → **Homestead** (about (1740,596)) → `UI_HOME_TOWN_MAIN`, the "Norton Town" hub.
  - The hub has Level / To-do tabs, Town Modes (Kanu's Wish, Cattisan's Abode, Neighbor Sharing, Worship Catlord, Donation, Ability Machine, Town Warehouse, Kanu's Playground), a Homestead Mode section, and the buttons **Go to Town** and **Go to Homestead** (about (1690,905)).
  - Going to the homestead loads the scene (`cave_mgr enter_cave`, `map_mgr ... FINISH JOURNEY`). **In the log, the homestead is "cave".**
- **Homestead HUD, different from the town's:**
  - top left: the homestead's name and level;
  - top bar: storage used/limit, coins, energy;
  - top right: Switch, More, Leave, minimap "Homestead";
  - bottom right: **Lumi** (paw, about (1808,700)), **Construction** (hammer), Party, Shop;
  - `UI_HOME_UPGRADE_BAR` is shown on arrival;
  - animals (Ranch) and crops (corn) are visible in the world.
- **Lumi's Hut** (`UI_HOME_PET_VIEW`): the game's own production automation.
  - Lumis (helpers) are assigned to buildings: Farmland (managing 5/60), Workshop (4/10), Ranch (2/4).
  - Side tabs: Task, Assign, Lumi, "Obtain Lumi" (**not pressed**; probably costs currency).
  - Farmland panel (`UI_HOME_PET_MANAGE_VIEW`): the Lumi on farmland, 0/5 working, and "Go to Assign".
- **Set Target**, the production picker (button at about (1556,880)):
  - Top row: a 6-slot target queue. A finished slot shows "16/16" with a red dot. **Pressing a finished slot collects it**: `UI_ITEM_GET_TIP` shows and the stock rises (milk 48→64→112, eggs 49→81), and the queue shifts left.
  - "Select Production Target" lists every building's products with current stock:
    - **Farmland (crops):** wheat 40, corn 19, carrot 97, tomato 10, a purple root 38, strawberry 0, a purple flower 103, a green crop 33;
    - **Ranch:** eggs, milk, one locked product;
    - **Bakery:** flour, buns, a pastry, cookies;
    - **Feed Processor** (2 Lumis): 3 feeds;
    - **Sauce Maker:** 3 jams and a sauce;
    - **Dairy Factory:** cream, butter, cheese (greyed: no Lumi);
    - further down (drag scroll works here too), all greyed: **Sugar Refinery, Juicer, Ice Cream Stall, Pizza Shop, Cake Shop**.
  - Tapping a product opens `UI_HOME_PET_MAKE_VIEW`: name, a **Cost** block (for crops: "Crops do not consume materials"), quantity −/+/max and Confirm.
  - Confirm adds it to the queue as "Producing 0/1". The log shows no production event (only the view closing), so progress is read on screen.
- **Module idea (Homestead):** on a schedule,
  1. go to the homestead, or open Lumi's Hut wherever it can be opened;
  2. collect every finished target;
  3. refill the queue with what crafting needs: crops cost nothing; processed goods have costs shown in the Cost block;
  4. leave.
  - Reuses: HUD navigation, slide clicks, drag scroll, the log (UI events and item-get tips), the number readers for counts and progress.
  - Open questions: how long a crop takes; whether Lumis "sleep" (zzz) until assigned work; whether the hut opens outside the homestead; what the Assign/Task tabs do.

## Shared homestead (user, 2026-10-15): design rules for the Homestead module
The farm crops and the Lumis **can be shared with another player (a homestead partner)**. Products can be collected or targets changed by the partner at any time, so there is competition and products can be lost.
- Homestead stock is volatile: keep only a short "fresh" window in Knowledge and re-read it right before crafting. The Make press is the final truth (a missing item is named in the log).
- Never remove or replace a target the companion did not queue. Use only free queue slots; if all 6 are busy, wait and check later.
- Collect policy, a setting: only what the craft plan needs (default) / everything / nothing. Never sweep the partner's goods by default.
- Remember our own requests (item, quantity, time). Products that vanish are logged as "taken by partner", not as errors, and are not retried forever.
- Fallback chain on "material missing":
  1. homestead product → queue it (crops are free) and retry with growing back-off, then give up and report;
  2. gatherable material → Gathering Helper queue;
  3. otherwise skip.
  Meanwhile the loop continues with recipes that need no missing goods, and the tray tells the user what is being waited for.
- Open: may the companion collect finished goods it did not queue when a recipe needs them? (ask the user)

## Log signals found in a full census of the homestead visit
- Arrival and scene change:
  - `map/map_mgr ... check_continue_after_portal` → `FINISH JOURNEY` (teleport done);
  - `shop/pay_mgr ... on_cave_role_ready` (the character is ready in the homestead);
  - `home/home_network [home_cp_warehouse] get_warehouse` (homestead storage loaded);
  - `main_ui_mgr [hide by cave setting] is_hide_left, is_hide_right` (homestead HUD flags);
  - `mount/mount_mgr do down mount` and `convoy/convoy_mgr leave_convoy` (travel side effects).
- Gathering status: `survey/survey_mgr auto req survey state` and `get_survey_list , survey num: N` (0 after Finish). Pair it with `set_auto_collect_status`.
- World events: `gameplay/gameplay_network [notify_change_feature]` and a `map_mgr` line naming a region (the "Mist Invasion" banner).
- **Not in the log:** setting or collecting Lumi production (only the UI events plus `UI_ITEM_GET_TIP`, with no item name). Production state must be read from the screen.
- **Privacy, must be excluded from any catalogue:**
  - `player/player_info_mgr` (other players' ids);
  - `team/*` action payloads (player ids);
  - `monitor/monitor_mgr` (telemetry);
  - plus the chat and analytics (ema_vendor) modules.

## Town (the home town), not explored yet
- Screens seen in the log while you played: `UI_TOWN_WAREHOUSE_MAIN_VIEW`, `UI_TOWN_WORKSHOP_MAIN`, `UI_TOWN_FACTORY_MAIN`, `UI_HOME_TOWN_INFO` (banner), `UI_HOME_ORDER` (orders).
- Town HUD: Shop, Switch, Leave, More, Bag, Aero, Godc.
- These are the homestead production chain behind the Homestead stock: a candidate **Homestead module** (collect outputs, refill production, deliver orders).

## Edge cases seen
- `UI_TIPS2` never logs a close. `UI_HOME_TOWN_INFO` stays open for the whole stay in town. Both are now overlays.
- Commit jobs (CraftLoop) cancel themselves when the user (or a proof script's focus messages) was active within the cooldown.
- A slot press during a closing tip can be swallowed (it selected nothing).
- The favourites row centre is at y≈530 (not 498).
- Team events (`team_net notify_team_suggest`) arrive at any time while in a team; they are not UI changes.

## Private messages (for the user's later idea: out-of-game DM notifications), structure only, no content kept
- Your own sends: `chat/chat_mgr.lua:458 send_msg_to_server` with the channel (`chat.send_team_chat`, `chat.send_guild_chat`, `chat.send_scene_chat`).
- Incoming: `chat/chat_list.lua:1305 [chat_list]add ... <channel> <numbers>` and `:1313 [chat_list]on_msg_show`, with no text.
- `friend/friend_mgr.lua:3618 [account_chat]do`: friend/account chat activity, the likely private-message signal (unconfirmed).
- `chat/chat_mgr.lua:2453 [chat mgr]try_translate_txt` **contains message text** (auto-translate). Never store it; at most show it transiently in a notification if the user wants that.
- `chat/chat_util.lua transfer_notice_to_recv`: system notices (e.g. gifts).
- To do when the feature is built: have another player send one test whisper and note which event fires and whether it carries sender and text. Keep everything in memory only (privacy rule).

## World navigation in the homestead
- Tapping the minimap opens `UI_SMALL_MAP` (position shown, "Tap the NPC name to navigate").
- Tapping a point on the map closes it and walks there. Log: `map/map_mgr [goto pos] scene_id ... pos: x 1 y`; a new move logs `[abort_travel] reason: stop_on_new_move`.
- Tapping the ground in the world also walks there.

## Manual crop plots: DEFERRED (user decision 2026-10-15: "depend on Lumi")
- Plots are shared, first come first served. A tap in the middle of a plot opens `UI_HOME_PLANT_VIEW`:
  - ripe: **Gather** / **Gather all** (all plots of that crop; overshooting is fine);
  - not ripe: the time until ripe.
  - Planting offers **Plant 1** / **Plant 25** (25 is fine even when fewer are needed).
- Tapping the plant model instead opens `UI_HOME_BUILD_INFO` (an info card with `Label_Time`, which closes when the press ends). Tapping the ground walks (`map_mgr [goto pos]`). The log tells hit from miss.
- Why deferred: picking the right plot per crop, ripeness, and hit-and-miss targeting add up to a project of their own.
- **Homestead supply = Lumi's Hut** (collect finished slots, queue needs; crops are free), within the shared-homestead rules above.

## Make tab selection (2026-10-15)
- Closing and reopening the Ability view does NOT keep a list selection: after the list walk had selected Roast Chicken (id 1044002), CraftLoop's reopen left another recipe selected, and Make named a missing **Water** (20330037), which Roast Chicken does not use.
- So a craft of a non-favourite never reopens the view: CraftLoop slot 0 leaves out its open step, and with an item id it reads the selected product's tip (the log names its id) before the first Make. A mismatch refuses with `selectedItem.changed`, and nothing is crafted.
- The list walk (find mode) inside a Craft job uses the off-screen anchor patch rule like a standalone scan. The opt-in now depends on the procedure (non-commit, ungated), not on the job; `crafting.loop` keeps the strict check.
- Roast Chicken found by the walk in 70 s (5 scrolls, 1 card press, 45 cards skipped by picture).

## Gathering Helper, live map (2026-10-15 evening) = the Gathering module
- Path: Ability view → right-hand **Gather** tab (1740,716) → cat **Gathering Helper** (156,330) → `UI_LIFESKILL_AUTO_COLLECT`.
- **The list:**
  - My Favorites first (hearted), then every material;
  - tiles about 98x100 in 5 fixed columns (x 344, 473, 601, 730, 858), rows 126 px apart, 280 px drag ≈ 2 rows;
  - **greyed tile = at its stock limit**: the game ignores a press on it.
- **Target picker:**
  - a tile press opens `UI_LIFESKILL_AUTO_COLLECT_SET` (name, "Stock Limit: 353/500"; the default target = limit − stock = the max);
  - the log shows the picker **only the first time** it is built;
  - a press on its picture opens the item tip: `item_tips_mgr ... <Name> <id>` (exact id, the same as the recipes' ids);
  - a press on the quantity opens a **keypad** (`UI_CALCULATOR`): clear, digits, OK; then the picker's Confirm queues "0/N" (no log line).
- **Begin:**
  - **teleports the character** to a gathering site, which gathers by itself;
  - log: `set_auto_collect_status 1`, `collect_mgr goto_collection begin <id>` … `end <id> true`;
  - **when complete**: `set_auto_collect_status 2`, and the game **reopens the Helper by itself** showing "Complete" and **Finish**;
  - Finish → `set_auto_collect_status 0`.
- **Timing:** 2 mushrooms 14 s, 20 mushrooms about 40 s (travel included); one pick can overshoot (+1..+2).
- **Gathering Duration** (hourglass on the Gather tab, `UI_COLLECT_TIME_VIEW`): "Limited Gathering Duration", 120/120 min, +20 min at 12:00 and 19:30 (UTC+1); tabs Gathering / Weather Gathering. **The Gathering Helper's auto-gathering does NOT use it** (user, 2026-10-15).
- **Engine lessons:**
  - the auto-collect halt protects the user's gathering, so Begin/Finish carry the step flag `changesAutoCollect`;
  - the Helper, picker, keypad and duration card are Esc-safe (a reset closes them instead of parking on them);
  - crafting works from a gathering site too.

## Make quantity and the shared keypad (2026-10-16)
- **Keypad:** a press on the Make view's quantity box (client 1095,872) opens the **same keypad** (`UI_CALCULATOR`, not logged) at the same place as the gathering picker's: clear (1145,330), digits, OK (1100,697).
- **The game clamps a typed number** to the most it can make: 999 typed with materials for 1 showed 1.
- **Selecting a recipe resets the quantity to 1**, so the count is typed AFTER the slot/card click and right before Make. One Make press then makes the whole batch: 3 Lime Water in one craft session, the job took 37 s instead of 1 m 47 s.
- **Typing runs in its own navigation procedure** (`crafting.qty`), not inside the commit procedure. Live, a quantity press anchored on "Materials Required" failed the strict check 25 times when the slide cut the anchor at the drawn monitor's edge (diagnostic crop half black). Make stays in the commit procedure, and is not pressed when typing failed.
- **Gathering picker "Stock Limit: a/b":** read as text (its own font spaces the slash). It gives the exact owned count and the limit, so a target = min(need − owned, limit − owned). Every probed material's count is recorded.

## 1.0 live findings (2026-10-16)
- **Popup:** the game's team recommendation `UI_TEAM_REOCMMEND_VIEW` can pop up over the Helper at any time (here right after a target was confirmed). Before Begin/Finish the module checks that the Helper list is shown: one Esc for an Esc-safe stray popup, or it reopens the Helper (the game keeps the queue).
- **Hidden stock line:** a long item description (e.g. Wisdom Essence) pushes the picker's "Stock Limit" line out of its box. The quantity box's default target is the free room (limit − owned), read as a fallback.
- **Craft batch cap:** the game makes at most **50 per craft session**; the typed loop types what is left each round (99 = 50 + 49).
- **Cross-monitor:** with the cursor on the left part of the game's monitor, a right-column target (x ≈ 1740) slides most of the window onto the left monitor, where the capture can't verify it. `gather.tab` is NavFixed (like `menu.ability`); any other press waits (`NotReadyReason.CrossMonitor`) instead of failing into a halt.
- **Peppercorn** (Clam Chowder) is a Homestead product (planting, 10 min), not in the Gathering Helper. The goal loop caps the goal by such a material and reports `limitedBy`.
- **Live results:** Clam Chowder ×150 made 16 + 99 Thick Fish Soup in batches, gathered with exact amounts (Toothclam to its 500 limit, Wisdom Essence exactly its room 20), and stopped on Peppercorn. Gather Mushroom ×10 on the final build: done.

## Gathering Helper: queue slots, pause and clamps (2026-10-16)

* **Status 3 = "Gathering Paused".** It happens when a trip stops in the middle, e.g. a target that cannot be gathered
  because its material is full.
  * The Helper then shows a Continue button with the same picture, at the same place, as Begin (NCC 1.000,
    `tests/TestData/images/gather_paused_full.png`).
  * The companion continues the trip; if it pauses again, it is left after 2 minutes.
* **Each queued slot shows `gathered/target` in green under its tile.**
  * Slot centres are x 852 + 165 i, y ~404 (reader `crafting.gather.queue`).
  * The target is what the game accepted: a typed count is clamped to the stock limit's room, and is 1 when there is no
    room at all. Scarlet Grape at its limit: 600 typed, 1 accepted.
* **Each queued tile has a small "-" button at its top-left** (tile centre − (55, 47)) that takes it out of the queue.
  The slots after it shift left. The companion presses it, last slot first, for a target cut to 1. That needs live
  confirmation: watch for `gather.unqueued` and `gather.unqueueMismatch`.
* **A broken gathering tool (durability 0) stops trips from starting.** No Finish button appears. Detection is still
  open: a capture of the Helper in that state is needed.
