# TOS companion: user manual

Version for Release 4.29 (2026-10-31). It is updated with every release that changes something you can see.

## What it is

A helper for Tree of Savior NEO crafting. It gathers materials with the game's own Gathering Helper and crafts recipes
through the Make tab, while you do something else. It reads the screen of the game window and the game's own log file.
It clicks by sending messages to the game window. It works for whichever character you play, and what it learns
(recipes, stock, item pictures) is shared by all of them: there is nothing to allow per character (since Release 4.33).

## What it never does

- Moves your mouse cursor, or brings the game window to the front.
- Reads game memory or game files, or sends its own network traffic.
- Presses Expand Inventory, Obtain Lumi, or anything in a shop, gacha or purchase window.
- Logs you in after a disconnect (that would need your password).
- Stores chat, other players' names, or any telemetry.
- Clears a safety stop by itself. You decide.

## Starting and stopping

- **Start:** start the companion the usual way. A tray icon appears near the clock. Left-click it to open the window.
- **Stop now:** tray menu, **Stop now**, or **Ctrl+Alt+Pause** anywhere. The job stops at once. Nothing runs again
  until you choose **Allow again (after Stop)** in the tray menu.
- **Pause / Resume:** tray menu. A paused job holds where it is and goes on after Resume.
- **Exit:** tray menu, **Exit**. Always exit this way before an update (see "Updates").

**With the game in front.** The companion also works while the game window is in front, as long as your mouse rests
on the game and you leave the mouse and keyboard alone. For each press the game window jumps under the mouse for about
half a second and comes back. A click or key you make during that half second goes into the game: the companion stops
that press at once and checks what happened, but it cannot stop your click. Rest the mouse on a quiet part of the game
(no button, not on the board or a list), at least a finger's width inside the window, not on the title bar. It starts
about 5 seconds after you click into the game, waits until you have not touched the mouse or keyboard for 2 seconds (3
seconds before it presses a key such as Esc), and never presses a letter key while the chat is open. A goal that is about
to press Make waits for you to stay away from the game: if you used the game meanwhile, the goal pauses and comes back.
It sees only the mouse and keyboard, not a game controller: if you play with a controller, switch this off. List scrolls
(dragging a list, for example the recipe list) are not done with the game in front yet: for those it still waits until
another window is in front of the game. If the game freezes in the middle of a press, the window stays moved until the game
answers (up to 30 seconds), then goes back and the companion stops with a message, because that press might then land
somewhere else. To switch this off, set `"foregroundSlide": false` in the `"input"` part of `C:\ProgramData\TOS-companion\config.json` and
restart the companion: it then waits whenever the game is in front, as before, and continues about 5 seconds after you
leave the game window.

## The window

| Tab | What it is for |
|---|---|
| Today | Your daily checklist (the window opens on it). What is left comes first, by section; done items fold into "Done today". Items tick by themselves when the game log shows them; counted ones show their progress (Arena 2/5, Gear rift 3/4, Orders 7/12). Click a circle to tick or untick it yourself (the Corridor, its pass and the Adventure pass are ticked by hand). Items with a stored count (Hugo train requests, demon trial rewards, Yggdrasil attempts) show it; right-click the row to correct it. The Special train shows "Ready to summon" at 6 fills: tick it when you summon it and it counts down its 2 hours. Items for certain days (Abyssal Mon/Wed/Fri, Demon trial Tue/Thu/Sat) only show on those days. **Coming up today** lists only today's timed events with their time and a countdown (raccoons, big raccoon, world boss, guild spa...); click one to mark it done; when done or missed it moves to Done today. **Homestead** shows Lumi's Hut and the helper: a red dot when empty or ready, a countdown for 2 hours after you queue items. When an event opens or a countdown ends while the game is not in front, the tray tells you. The list resets at 07:00 (Worship at 02:00, Honor War on Monday 07:00). **Edit list** chooses what is on the list, renames items and adds your own. `Companion.Cli dailies` prints the list. It only reads the game log: it never clicks. Items marked "learning" are still being checked against your play. |
| Craft | Choose a recipe and how many, then **Start crafting**. "Forever" crafts until you press Stop. "Only from my stock" never gathers. **Add to queue** puts the recipe and the number on the Queue tab. |
| Queue | Goals made one after another (0 = forever). Up to 11 fit at once (one running, ten waiting); more show "refused (refused.queueFull)" and do not run. The list stays in the window after Start (it is not kept after Exit): to run it again later, remove the goals that already ran first. "Gather everything" refills every raw material your known recipes use, up to its stock limit. |
| Rules | "Keep these in stock": rule mode makes a recipe whenever you have fewer than the number you set. Press **Save rules** after a change (not possible while a job runs). It checks every 10 minutes, and about a minute after your character auto-eats food (so food rules refill sooner). A rule that keeps failing is checked less often, and the tab says why. "You have (last check)" is the count it read today. Rule mode stays on, also after a restart, until you turn it off; it starts only when no goal runs or waits. |
| Activity | What happened today and yesterday, in plain sentences. After 20 minutes or more away from the game, a card at the top says what the companion did meanwhile (you also get a notification when you come back). |
| Stats | The last 7 days: crafted, crafting time, crafts per hour, gathering trips, items per trip, pauses, safety stops, finished jobs, a chart, and the week's top items. |
| Settings | Options and switches. |

Gather everything (Queue tab): it first reads how full every material is (one pass over the Gathering Helper, nothing
gathered yet), then each trip takes the 4 emptiest materials, until each is full. A material the helper does not show
(greyed when full, or not offered where you are) is left out and listed at the end. When you play while it runs (or
the game is minimized), it waits: an interrupted trip is paused and tried again (from 30 seconds up to 5 minutes, longer
when the engine's safety allowance is close), and it gives up only after 75 minutes without a trip that gathered. When
the only problem is where your mouse rests, it waits up to 3 hours instead. A slow trip is not a failed one: a trip may
take 10 minutes plus 25 seconds per item, and while the character is still out gathering it pauses and comes back for
the trip (a crafting goal does the same and crafts what it can meanwhile). If your mouse cuts a scroll of the Gathering
Helper list short, it scrolls again instead of taking it for the end of the list, and a tile press your mouse spoiled is
pressed once more. A goal whose recipe needs something that cannot be gathered (a homestead good) and that is gone ends
at once with "The goal is limited by: rosemary x118" instead of gathering the other materials first. The game takes at most about 200 of a material per trip (fewer for some), 4 materials per
trip, so a full refill can take an hour or more. The status line at the top says which trip it is on, how many
materials are still to fill, and what the character is out gathering. Pressing the button again while it runs does
nothing (it says so).

Kitty Tea House helper (tray menu "Kitty Tea House helper...", or `Companion.Cli show teahouse`): open Kitty Tea
House in the game (What's On), then open the helper. It never clicks anything: you place every piece. It shows a copy
of the board with the best square outlined in green and a sentence such as "Place the Cookie at row 3, column 4" or
"Swap with the tray, then place the Oven at row 5, column 2". A piece it has not seen before shows a red "?": click
that square (or the current piece on the right) and choose what it is, once. It learns the points and how often each
piece comes as you play. It pauses while the companion runs a job.

**Auto-play** (tick "Auto-play" in the helper): the companion presses each move for you, after the helper's full
look ahead, and reads the board again. It presses through its usual safe clicks: it never moves your mouse and never
brings the game to the front. Either click once on another program's window that is in front of the game (Notepad, a
browser, File Explorer: NOT the helper or any companion window, which are never used as cover) and leave the mouse still
in the middle of that window, or keep the game in front and rest the mouse on a quiet part of the game, away from the
board (see "With the game in front": the window then jumps for each move). If a move cannot be pressed, the status line says what the companion is waiting for. It presses only the 36 board squares, only while What's On shows the Kitty Tea
House page; everything else on that screen (event list, rewards, Rankings, Shop, close) is off limits. It never presses
Esc there (Esc closes the minigame): when part of the page is hidden for a moment (a banner scrolling across the top),
it presses from another picture of the page or not at all. Each press counts only when the game's log shows it. When a round ends, the end screen appears: until the companion knows its
Restart button, auto-play stops there and keeps a picture of the end screen (chat area blacked out) so the button can
be taught; press Restart yourself and tick Auto-play again. Auto-play switches itself off after three presses in a row
that did not land, when another job is running, after you press Stop, or after a safety stop, and says why. Closing
the helper window ends it too. Rounds are not cut short: practice games showed that a round's fate cannot be told
early enough to save time.

**Fast** (tick "Fast" in the helper): no look-ahead. The learned brain picks each move from the board alone (the way
it plays in practice), so auto-play presses at once instead of thinking 2 to 15 seconds; the advice line says "fast: no
look-ahead" and no round outlook is shown. A move then takes about 3 to 4 seconds: finding the board, the press itself
with the window slide, a short check for windows the press opened, and reading the board after the game's animation.

Craft tab buttons:

- **Scan favourites:** reads your My Favorites recipes (about 10 seconds each). If the game left the Make list
  scrolled down (it does after cooking), it drags the list back to the top once and reads again. It drags nothing
  while another game window is on top. Goals and the Make-tab counts use the same step.
- **Scan all recipes:** walks the whole Make list (5 to 20 minutes). Run it after you learn new recipes.
- **Learn selected:** reads the recipe that is selected in the game.

## Messages you may see

| Message | What it means | What to do |
|---|---|---|
| The companion is waiting: it cannot click the game while your mouse rests on another monitor | It never moves your mouse; it slides the game under it, and it cannot do that onto another screen. | Leave the mouse anywhere on the game's monitor (or move it back there); it carries on by itself. |
| The companion is waiting: it cannot click the game from where your mouse rests | The mouse rests on a window edge or the taskbar, or the window under it is not the one in front (with the game in the background). | Click another program's window (a browser) on the game's monitor and leave the mouse in its middle, or click the game and rest the mouse on it. A goal or Gather everything waits up to 3 hours for this. |
| The game is in front: rest the mouse on the game itself, away from its edges and title bar | The game is in front but the mouse is on its title bar, near its edge, or on another window. | Rest the mouse on a quiet part of the game. |
| The game is in front: keep your hands off the mouse and keyboard for a few seconds | You used the mouse or keyboard, or just clicked into the game. | Leave them still; it carries on after 2-5 seconds. |
| You used the mouse or keyboard while the game was in front, so that press stopped | Your input reached the game during a press. | Nothing: it checks the screen and goes on. Stay still while it works. |
| A goal pauses: "the screen it needs is not showing" | You are using the game (another window is on top of the Make view). | Nothing: it waits (up to 3 hours) and goes on when the Make view is back. |
| A shop, exchange, purchase, invite or match window was opened in the game | The companion never touches these windows; it waits. | Close it. If it is already closed, press Esc once in the game with no window open. |
| Gathering gear wore out: ticked "Don't show again today" and pressed Ignore | The gear notice came up; the companion answered it. Gathering goes on without the gear bonus. | Replace the gear in the game when you can: the notice names it. Until then the notice can come back every minute or so (the tick does not hold), and each one holds gathering for a few seconds. |
| Gear notice still on screen (not dismissed) | The companion could not press Ignore this time (for example you paused or stopped in between). | Press Ignore in the game, or let the companion try again. |
| Could not see X in the Gathering Helper list this trip | The list was not fully readable (a popup, a scroll that did not settle). X is not ruled out. | Nothing: it is tried again next trip. |
| A step kept failing: the goal stopped before it became a safety stop | The same step failed twice in 30 minutes. The goal ends with the reason instead of retrying into a safety stop. | Look at the Activity line's reason. Start the goal again when the cause is gone. |
| Your keyboard layout changed since the companion connected | The companion cannot press game keys with another layout. | Switch the layout back (Alt+Shift). |
| Gather everything: stopped with materials left untried | A trip failed twice the same way. The untried materials are listed. | Read the reason; start again later. |
| The previous run ended without a clean exit | The companion was closed by Task Manager, by Windows, or crashed. | Nothing needed. If it happens again, tell the developer. The diagnostics folder has details. |
| The wrong recipe was selected (a click was disturbed): selected it once more | Your mouse moved while the companion clicked a favourite, so the click landed on another recipe. It is checked before every Make, so nothing wrong is crafted. | Nothing. |
| The same plan came up again with nothing made: the goal stopped | A round made none of the goal and gathered nothing, and the next plan was the same. Repeating it would do the same. | Read the lines before it (usually a missing material). |
| Could not read your counts on the Make tab now | The companion waits instead of planning from old counts. | Nothing; it tries again after a short pause. |
| Gathering failed twice the same way (code): crafting from stock only | The same gathering problem happened twice in a row. The goal goes on with what you have. | Check your gathering tool and the Gathering Helper. |
| Rule mode: X could not be made (reason): checking it again in N min | A rule is blocked, for example by a missing material. It is checked less often (10, 20, 40, then every 60 min) until something changes. | Fix the reason, or wait. |
| A game window you opened is still open | The companion waits for you to close it. | Close it in the game. |
| The game is not windowed and maximized at 1920x1009 | The screen reading needs this window size. | Maximize the game window. |

## Safety stops

A safety stop ("STOPPED FOR SAFETY") means something unexpected happened, for example a click that may have landed in
the game world. The companion does nothing until you choose **Continue after a safety stop...** in the tray menu. Read
the reason first. Every safety stop is in the journal with its cause.

Since Release 1, these no longer cause safety stops; they end the step for later instead:

- presses that were cancelled before the click went down (your cursor moved, the check before the click failed);
- a dialog that Esc cannot close (the reset tries twice, then gives up instead of repeating);
- a key the game refuses because the keyboard layout changed.

## Troubleshooting

| Problem | Try this |
|---|---|
| The window says "THE GAME IS NOT RUNNING" while it runs | Since Release 4.33 it sees the game as soon as it is open again. If it still says so, check that only one game window is open, then restart the companion. |
| Clicks in the game stop working after the companion acted | Click the game window once; the companion also repairs this by itself within a few seconds. |
| Nothing happens for a long time | Activity tab: the latest line says what it waits for. The tray menu's first line shows the status. |
| A recipe says "not found in the Make list" | Since Release 1 this means the walk really reached the end of the list. Scan all recipes once. |
| A material is called "not gatherable" | It is not in the Gathering Helper (for example a homestead crop). Since Release 1 this is said only after a complete walk. |
| A goal made fewer than asked | The end line gives the reason. Since Release 2, a material that runs out mid-goal (for example Golden Juice: 300 is enough for 60 cakes) is gathered again and the goal goes on. |
| "refused (refused.queueFull)" on the Queue tab | More than 11 goals at once. Start the rest after some have finished. |

## Updates

1. Exit the companion from the tray (wait until no job runs).
2. The new build is installed and trusted.
3. Start the companion again.

Each update was described in release notes (kept private). The previous build is kept, so an update can
be rolled back.
