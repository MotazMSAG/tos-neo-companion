using Companion.Contracts;
using Companion.Contracts.Safety;
using Companion.GameIO.Native;

namespace Companion.GameIO.Safety;

/// <summary>A hard rule was about to be broken by our own code. Nothing was sent; the caller aborts safely.</summary>
internal sealed class HardRuleViolationException(string code) : InvalidOperationException("hard rule violation: " + code)
{
    /// <summary>Short code for the journal.</summary>
    public string Code { get; } = code;
}

/// <summary>Outcome of one sent (SendMessageTimeout / SendNotifyMessage) message.</summary>
internal enum SendOutcome
{
    /// <summary>Delivered (returned in time).</summary>
    Ok,
    /// <summary>Timed out or aborted because the game is hung.</summary>
    Timeout,
    /// <summary>ERROR_ACCESS_DENIED (UIPI / integrity mismatch).</summary>
    AccessDenied,
    /// <summary>Not sent: a fake unfocus while the game PID is really foreground (R2).</summary>
    SkippedForeground,
    /// <summary>Other failure.</summary>
    Failed,
}

/// <summary>
/// Runtime asserts that are not bound to a validated game window (SPEC 1.5): GetAsyncKeyState only for VK 1, 2, 4;
/// OpenProcess only with PROCESS_QUERY_LIMITED_INFORMATION.
/// </summary>
internal static class Guards
{
    /// <summary>True if the mouse button is down (high bit). Throws for any VK other than 1, 2, 4.</summary>
    public static bool IsMouseButtonDown(IWin32 win32, int virtualKey)
    {
        if (!StaticRules.GetAsyncKeyStateAllowedKeys.Contains(virtualKey))
        {
            throw new HardRuleViolationException("getAsyncKeyState.vk");
        }

        return (win32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    /// <summary>True if any mouse button is down.</summary>
    public static bool AnyMouseButtonDown(IWin32 win32) =>
        IsMouseButtonDown(win32, Win32Const.VK_LBUTTON)
        || IsMouseButtonDown(win32, Win32Const.VK_RBUTTON)
        || IsMouseButtonDown(win32, Win32Const.VK_MBUTTON);

    /// <summary>Executable path of a process. Throws for any access other than 0x1000.</summary>
    public static string? QueryImagePath(IWin32 win32, int pid, uint access)
    {
        if (access != StaticRules.OpenProcessAllowedAccess)
        {
            throw new HardRuleViolationException("openProcess.access");
        }

        return win32.QueryProcessImagePath(pid, access);
    }

    /// <summary>True if (VK, scan) is an exact whitelisted pair and not hard-denied.</summary>
    public static bool IsKeyAllowed(KeyCode key) =>
        HardRules.AllowedKeys.Values.Contains(key) && !IsKeyDenied(key);

    /// <summary>True if the VK or the scan code of a hard-denied key (Enter, Y, I) is involved.</summary>
    public static bool IsKeyDenied(KeyCode key) =>
        HardRules.DeniedKeys.Any(d => d.VirtualKey == key.VirtualKey || d.ScanCode == key.ScanCode);
}

/// <summary>
/// The single choke point for everything that reaches the game window (R2, R5, SPEC 1.5). Every acting call asserts:
/// the HWND is the validated game window; the message is in <see cref="HardRules.AllowedMessages"/> (WM_SIZE only in
/// emergency mode) with its exact wParam/lParam shape; mouse lParams lie inside the client; keys are exact whitelisted
/// (VK, scan) pairs and never denied; SetWindowPos uses hWndInsertAfter = 0 and an allowed flag set (emergency flags
/// only in emergency mode); PrintWindow uses PW_RENDERFULLCONTENT. A fake unfocus is never sent while the game PID is
/// really foreground. Violations throw <see cref="HardRuleViolationException"/> before anything is sent.
/// Thread-safe (the watchdog and the engine thread both use it).
/// </summary>
internal sealed class ChokePoint
{
    private readonly IWin32 _win32;
    private int _emergencyDepth;
    private int _foregroundDepth;

    public ChokePoint(IWin32 win32, long gameHwnd, int gamePid, SizeI clientSize)
    {
        if (gameHwnd == 0)
        {
            throw new HardRuleViolationException("choke.noGame");
        }

        _win32 = win32;
        Game = gameHwnd;
        GamePid = gamePid;
        ClientSize = clientSize;
    }

    /// <summary>The validated game window.</summary>
    public long Game { get; }

    /// <summary>The game process id.</summary>
    public int GamePid { get; }

    /// <summary>Client size used for the mouse lParam assert.</summary>
    public SizeI ClientSize { get; }

    /// <summary>True inside the emergency restore ladder.</summary>
    public bool InEmergency => Volatile.Read(ref _emergencyDepth) > 0;

    /// <summary>Enters the emergency restore ladder (allows WM_SIZE and the 0x0214/0x4214 flags) until disposed.</summary>
    public IDisposable EnterEmergency()
    {
        Interlocked.Increment(ref _emergencyDepth);
        return new EmergencyScope(this);
    }

    /// <summary>True inside a foreground-mode input (SPEC 7.3b): every focus message is refused.</summary>
    public bool InForegroundInput => Volatile.Read(ref _foregroundDepth) > 0;

    /// <summary>
    /// Marks a foreground-mode input until disposed: the game has real focus, so ANY focus message is a hard-rule violation
    /// (R2; a fake unfocus while the game is in front leaves it ignoring the user's clicks, incident 3). The callers never
    /// send one in this mode; this makes it independent of the foreground check's timing.
    /// </summary>
    public IDisposable EnterForegroundInput()
    {
        Interlocked.Increment(ref _foregroundDepth);
        return new ForegroundScope(this);
    }

    /// <summary>True when the foreground window belongs to the game PID (FOC-8: PID, not HWND).</summary>
    public bool IsGameForeground()
    {
        var fg = _win32.GetForegroundWindow();
        return fg != 0 && (fg == Game || _win32.GetWindowProcessId(fg) == GamePid);
    }

    /// <summary>PostMessage after the asserts. Focus messages may not be posted (they are sent).</summary>
    public bool Post(long hwnd, uint message, long wParam, long lParam, out int lastError)
    {
        AssertMessage(hwnd, message, wParam, lParam);
        if (IsFocusMessage(message, wParam, lParam))
        {
            throw new HardRuleViolationException("post.focusMessage");
        }

        return _win32.PostMessage(hwnd, message, wParam, lParam, out lastError);
    }

    /// <summary>SendMessageTimeout (SMTO_ABORTIFHUNG|SMTO_BLOCK, 250 ms) for focus messages and WM_NULL only.</summary>
    public SendOutcome Send(long hwnd, uint message, long wParam, long lParam)
    {
        AssertMessage(hwnd, message, wParam, lParam);
        if (!(IsFocusMessage(message, wParam, lParam) || message == HardRules.WM_NULL))
        {
            throw new HardRuleViolationException("send.notFocusOrNull");
        }

        if (InForegroundInput && IsFocusMessage(message, wParam, lParam))
        {
            throw new HardRuleViolationException("focus.foregroundInput");
        }

        if (IsFocusOffMessage(message, wParam, lParam) && IsGameForeground())
        {
            return SendOutcome.SkippedForeground;
        }

        var ok = _win32.SendMessageTimeout(
            hwnd, message, wParam, lParam, Win32Const.SMTO_ABORTIFHUNG | Win32Const.SMTO_BLOCK,
            (uint)HardRules.SendMessageTimeout.TotalMilliseconds, out var err);
        if (ok)
        {
            return SendOutcome.Ok;
        }

        return err switch
        {
            Win32Const.ERROR_ACCESS_DENIED => SendOutcome.AccessDenied,
            Win32Const.ERROR_TIMEOUT or 0 => SendOutcome.Timeout,
            _ => SendOutcome.Failed,
        };
    }

    /// <summary>SendNotifyMessage (nonqueued, does not block on a hung game): fake unfocus messages only (forced order).</summary>
    public SendOutcome Notify(long hwnd, uint message, long wParam, long lParam)
    {
        AssertMessage(hwnd, message, wParam, lParam);
        if (!IsFocusOffMessage(message, wParam, lParam))
        {
            throw new HardRuleViolationException("notify.notFocusOff");
        }

        if (InForegroundInput)
        {
            throw new HardRuleViolationException("focus.foregroundInput");
        }

        if (IsGameForeground())
        {
            return SendOutcome.SkippedForeground;
        }

        if (_win32.SendNotifyMessage(hwnd, message, wParam, lParam, out var err))
        {
            return SendOutcome.Ok;
        }

        return err == Win32Const.ERROR_ACCESS_DENIED ? SendOutcome.AccessDenied : SendOutcome.Failed;
    }

    /// <summary>SetWindowPos with hWndInsertAfter = 0 and an allowed flag set.</summary>
    public bool SetWindowPos(long hwnd, long insertAfter, int x, int y, int cx, int cy, uint flags, out int lastError)
    {
        AssertHwnd(hwnd);
        if (insertAfter != 0)
        {
            throw new HardRuleViolationException("swp.insertAfter");
        }

        var allowed = HardRules.AllowedSetWindowPosFlags.Contains(flags)
            || (InEmergency && HardRules.EmergencySetWindowPosFlags.Contains(flags));
        if (!allowed)
        {
            throw new HardRuleViolationException("swp.flags");
        }

        if ((flags & HardRules.SWP_NOSIZE) == 0 && (cx <= 0 || cy <= 0))
        {
            throw new HardRuleViolationException("swp.size");
        }

        return _win32.SetWindowPos(hwnd, insertAfter, x, y, cx, cy, flags, out lastError);
    }

    /// <summary>PrintWindow(PW_RENDERFULLCONTENT) of the game client.</summary>
    public byte[]? PrintClient(long hwnd, uint flags, RectI windowRect, PointI clientOrigin, SizeI clientSize)
    {
        AssertHwnd(hwnd);
        if (flags != HardRules.PW_RENDERFULLCONTENT)
        {
            throw new HardRuleViolationException("printWindow.flags");
        }

        return _win32.PrintWindowClient(hwnd, flags, windowRect, clientOrigin, clientSize);
    }

    /// <summary>All message asserts (public for tests).</summary>
    public void AssertMessage(long hwnd, uint message, long wParam, long lParam)
    {
        AssertHwnd(hwnd);
        var allowed = HardRules.AllowedMessages.Contains(message)
            || (InEmergency && HardRules.EmergencyOnlyMessages.Contains(message));
        if (!allowed || HardRules.ForbiddenMessages.Contains(message))
        {
            throw new HardRuleViolationException("message.notAllowed");
        }

        switch (message)
        {
            case HardRules.WM_NULL:
                if (wParam != 0 || lParam != 0)
                {
                    throw new HardRuleViolationException("message.nullParams");
                }

                break;
            case HardRules.WM_MOUSEMOVE:
            case HardRules.WM_LBUTTONDOWN:
            case HardRules.WM_LBUTTONUP:
                // DOWN carries MK_LBUTTON, UP nothing; a MOUSEMOVE carries nothing or, while a drag holds the button
                // (SPEC 7.3a, proof c14), MK_LBUTTON.
                var expectedW = message == HardRules.WM_LBUTTONDOWN ? HardRules.MK_LBUTTON : 0;
                var dragMove = message == HardRules.WM_MOUSEMOVE && wParam == HardRules.MK_LBUTTON;
                if (wParam != expectedW && !dragMove)
                {
                    throw new HardRuleViolationException("mouse.wParam");
                }

                if (!MouseLParamInsideClient(lParam, ClientSize))
                {
                    throw new HardRuleViolationException("mouse.lParamOutsideClient");
                }

                break;
            case HardRules.WM_KEYDOWN:
            case HardRules.WM_KEYUP:
                AssertKey(message, wParam, lParam);
                break;
            case HardRules.WM_SIZE:
                if (wParam != HardRules.SIZE_MAXIMIZED
                    || lParam != (((long)HardRules.ExpectedClientHeight << 16) | (long)HardRules.ExpectedClientWidth))
                {
                    throw new HardRuleViolationException("wmSize.params");
                }

                break;
            default:
                if (!IsFocusMessage(message, wParam, lParam))
                {
                    throw new HardRuleViolationException("focus.params");
                }

                break;
        }
    }

    /// <summary>True if the packed (y &lt;&lt; 16 | x) lParam lies inside a client of the given size.</summary>
    public static bool MouseLParamInsideClient(long lParam, SizeI client)
    {
        if (lParam < 0 || lParam > 0xFFFFFFFFL)
        {
            return false;
        }

        var x = (int)(short)(lParam & 0xFFFF);
        var y = (int)(short)((lParam >> 16) & 0xFFFF);
        return x >= 0 && y >= 0 && x < client.Width && y < client.Height;
    }

    /// <summary>True for a message of the fake focus or unfocus sequence (exact wParam/lParam).</summary>
    public static bool IsFocusMessage(uint message, long wParam, long lParam) =>
        HardRules.FocusOnSequence.Contains(new FocusMessage(message, wParam, lParam))
        || HardRules.FocusOffSequence.Contains(new FocusMessage(message, wParam, lParam));

    /// <summary>True for a message of the fake unfocus sequence.</summary>
    public static bool IsFocusOffMessage(uint message, long wParam, long lParam) =>
        HardRules.FocusOffSequence.Contains(new FocusMessage(message, wParam, lParam));

    private static void AssertKey(uint message, long wParam, long lParam)
    {
        if (wParam is < 0 or > 0xFFFF)
        {
            throw new HardRuleViolationException("key.vk");
        }

        var scan = (ushort)((lParam >> 16) & 0xFF);
        var key = new KeyCode((ushort)wParam, scan);
        if (Guards.IsKeyDenied(key))
        {
            throw new HardRuleViolationException("key.denied");
        }

        if (!Guards.IsKeyAllowed(key))
        {
            throw new HardRuleViolationException("key.notWhitelisted");
        }

        var expected = message == HardRules.WM_KEYDOWN ? HardRules.KeyDownLParam(key) : HardRules.KeyUpLParam(key);
        if (lParam != expected)
        {
            throw new HardRuleViolationException("key.lParam");
        }
    }

    private void AssertHwnd(long hwnd)
    {
        if (hwnd != Game || hwnd == 0)
        {
            throw new HardRuleViolationException("hwnd.notGame");
        }
    }

    private sealed class ForegroundScope(ChokePoint owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._foregroundDepth);
            }
        }
    }

    private sealed class EmergencyScope(ChokePoint owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._emergencyDepth);
            }
        }
    }
}
