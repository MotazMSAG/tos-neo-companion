using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.Engine.Internal;

/// <summary>Fixed user texts (SPEC 6.9). No game text, no chat: only the subject code is substituted.</summary>
internal static class Messages
{
    /// <summary>HALT text for a code.</summary>
    public static string HaltText(HaltCode code, string? subject)
    {
        var s = string.IsNullOrEmpty(subject) || !JournalSchema.IsCode(subject) ? "?" : subject;
        return code switch
        {
            HaltCode.ForbiddenUi => "A shop or purchase screen opened. I closed it once and stopped. Nothing was bought. Check the game.",
            HaltCode.EscOpenedUi => "Pressing Esc opened a screen instead of closing one. I stopped; please look at the game.",
            HaltCode.GuideBan => "A tutorial step is blocking the game's menus. Finish it in the game, then clear the stop in the tray.",
            HaltCode.ExtraGameWindow => "The game opened an extra window (security or login dialog). I stopped.",
            HaltCode.CursorClipChanged => "The game tried to lock your mouse while I was working. I let go and stopped.",
            HaltCode.RestoreFailed => "I could not put the game window back. Double-click the game's title bar to maximize it again.",
            HaltCode.GameSelfResize => "The game changed its own window size during a step. I stopped; maximize the game again.",
            HaltCode.LayoutChanged or HaltCode.RecalibrateTemplate =>
                $"The button '{s}' is not where I expected; the screen layout changed. Run Calibrate, then update the template.",
            HaltCode.WorldPress => "A click landed in the game world instead of a button. I stopped so nothing else happens.",
            HaltCode.GameStoppedMidPress =>
                "The game stopped responding in the middle of a press while it was in front. After 30 seconds I moved its window back, so that press may land somewhere else when the game wakes up. Look at the game before you allow me again.",
            HaltCode.FocusFailed or HaltCode.IntegrityMismatch =>
                "The game does not react to me. Run both the game and the companion as administrator (one UAC prompt).",
            HaltCode.IdentityChanged => "The game restarted while I was working. Start the job again.",
            HaltCode.LogStalled => "The game log stopped updating. I stopped because I can no longer check what happens.",
            HaltCode.RepeatedState or HaltCode.BudgetExceeded or HaltCode.CountedFailuresExhausted or HaltCode.PreconditionLostExhausted =>
                $"I tried '{s}' several times and it did not work. I stopped; details are in the diagnostics folder.",
            HaltCode.CraftStoppedEarlyTwice or HaltCode.BlockedCraftSpot =>
                "Crafting stopped early twice (inventory full, out of energy, or a blocked spot). I stopped.",
            HaltCode.AutoCollectChanged => "Auto-gathering changed while I was working. I stopped so I do not interrupt it.",
            HaltCode.UnknownUiTwice => "Screens I do not know opened twice. I stopped; run a Survey so I can learn them.",
            HaltCode.TooManyWindowIncidents => "The game window moved unexpectedly several times. I stopped.",
            HaltCode.TamperedFiles => "Companion files changed since you trusted this build. Check them, then choose 'Trust this build' in the tray.",
            _ => "Something went wrong inside the companion. I stopped; details are in the diagnostics folder.",
        };
    }

    /// <summary>Non-HALT notifications (fixed text per code).</summary>
    public static Notification Note(string code) => code switch
    {
        "park.userUiOpen" => new(NotificationKind.NeedsUser, code,
            "A game window you opened is still open. I paused the job; close it in the game and I will continue."),
        "park.long" => new(NotificationKind.NeedsUser, code,
            "A job has been waiting for 30 minutes. It continues by itself once the game is idle."),
        "park.expired" => new(NotificationKind.Warning, code,
            "A waiting job expired after 4 hours and was cancelled."),
        "wait.displayMode" => new(NotificationKind.NeedsUser, code,
            "The game is not windowed and maximized at 1920x1009. I am waiting; maximize the game window."),
        "wait.foreignBlockingUi" => new(NotificationKind.NeedsUser, code,
            "A shop, exchange, purchase, invite or match window was opened in the game. I am waiting; close it yourself. Some of these windows never report that they closed (incident 25): if it is already closed, press Esc once in the game with no window open, and I carry on."),
        "wait.autoCollect" => new(NotificationKind.NeedsUser, code,
            "Auto-gathering is on or its state is unknown. I am waiting; confirm in the tray if this status means 'not gathering'."),
        "wait.keyboardLayout" => new(NotificationKind.NeedsUser, code,
            "Your keyboard layout changed since the companion connected, so it cannot press game keys. Switch it back (Alt+Shift)."),
        "procedure.pending" => new(NotificationKind.Info, code,
            "Part of the job could not be done yet (a button still needs calibration). Details are in the journal."),
        _ => new(NotificationKind.Info, "engine.info", "The companion needs your attention. Details are in the journal."),
    };
}
