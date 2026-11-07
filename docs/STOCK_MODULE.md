# Stock module design (roadmap #4)

**Goal:** know every material you own without opening each recipe. Read the Ability screen's Stock tab once per job, learn item names and ids from the game log the first time an icon is seen, then only read counts.

## What the game shows (live, 2026-10-15; full-size captures in proofs\out)
- **Path:** HUD menu → Ability (`UI_LIFESKILL_MAKE_VIEW`) → Stock tab (right column, about (1738,578)).
  - The tab switch writes **no** UI event; the press is logged as `UpdateModule LostFocuse Name: Label_Text`.
- **Sub-tabs** at y≈226: Ability (about 380), Homestead (610), Tailor (842), Hyperweather (1072). Each press is logged as `Label_Text`.
- **Captures:**
  - `stock_ability_top_full.png`, `stock_ability_s1_full.png`, `stock_ability_s2_full.png`, `stock_ability_s3_full.png`: the Ability grid, then after 1–3 drags of 300 px up at (940,700);
  - `stock_homestead_full.png`, `stock_tailor_full.png`, `stock_hyperweather_full.png`.
  - Half-scale previews: `explore_stock.png`, `explore_stock_home.png`.
- **Grid:**
  - 6 columns of item icons (about 110 px), grouped by section headers (Crystal, Essence, ...);
  - the count sits at the icon's bottom right in white digits with a dark outline (a new font, not parchment);
  - faded icons without a count mean 0;
  - the grid scrolls by drag (the existing drag primitive and RecipeList-style shift measurement apply).
- **Homestead sub-tab footer:**
  - storage used/limit (e.g. 914/1270, then 1010/1270, 1015/1270 after collecting);
  - three building materials (wood 221, a second 229, nails 256);
  - "Expand Inventory": **FORBIDDEN, never press**;
  - "Treebond Warehouse".
- **Item identity:**
  - pressing an icon opens `UI_ITEM_TIPS_PASS_TOUCH`;
  - the log writes `item/item_tips_mgr.lua:200 ... print name, uuid, id : <Name> <ItemId>` (e.g. Psychic Vug 10102013);
  - Esc closes the tip.

## Design (reuse first)
- **Job "ScanStock":**
  1. Open Ability (existing OpenAbility procedure) and press the Stock tab.
  2. For each sub-tab: scroll to the top, then per screen:
     - find the icons (grid detector);
     - for each icon whose fingerprint is not in the knowledge icon→item map, press it, take the fresh item tip (the shared ProductTip helper, generalised to "item tip"), then Esc;
     - read the count (new font; faded = 0; no count = Unknown unless faded);
     - drag up; stop when the shift is 0 (list end), as ScanRecipeList does.
- **Knowledge:**
  - write stock observations keyed by item id (from Part B of the reader/knowledge work), with source "screen:stock/<subtab>";
  - mark homestead-tab items as shared/volatile (short freshness), per the shared-homestead rules in DISCOVERIES.md.
- **Data first:** templates (Stock tab, sub-tab labels), procedures (open stock, sub-tab presses, drag) and readers (grid + count font) go in the feature's data files.
- **Safety:** add Expand Inventory's UI name to the forbidden list once seen. Never press the footer buttons. Navigation only, so no gate is needed.
- **Pacing and cost:**
  - first run: one tip press plus Esc per new icon (many presses, human pace);
  - later runs: counts only, with no presses;
  - journal codes, ids and counts only.
- **Tests:** reader tests on the full captures (counts right or Unknown, never wrong), grid detection per sub-tab, scan-loop tests with fakes (new icon → tip pressed once; known icon → no press; faded → 0; list end).
