namespace Companion.Contracts.Safety;

/// <summary>
/// Data for the static rules test (tests\Companion.GameIO.Tests\StaticRulesTests) that scans every *.cs file under
/// companion\src, companion\features and companion\tools. The token scans exclude src\Companion.Contracts (it only
/// names these APIs in constants and docs) and tests\; the P/Invoke scan covers every project including Contracts
/// (which may declare none). See SPEC section 1.
/// </summary>
public static class StaticRules
{
    /// <summary>
    /// Tokens that must not appear anywhere in scanned source (code, strings or comments; case-sensitive ordinal search).
    /// Covers R1 (cursor), R2 (activation / z-order / show state), R3 (other window moves), R5 (injection, memory,
    /// network, code loading, registry writes), R6 (emulators) and the UI rules (no focus-stealing dialogs).
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenTokens =
    [
        // R1: real cursor and input injection
        // (ClipCursor is caught by the P/Invoke allowlist; these spellings avoid matching the allowed GetClipCursor)
        "SetCursorPos", "SetPhysicalCursorPos", "SendInput", "mouse_event", "keybd_event", "BlockInput",
        " ClipCursor(", ".ClipCursor(", "(ClipCursor(", "\"ClipCursor\"",
        "SetWindowsHookEx", "RegisterRawInputDevices", "Cursor.Position", "Cursor.Clip", "SendKeys", "WH_JOURNAL",
        // R2: activation, z-order, show state
        "SetForegroundWindow", "SetActiveWindow", "SetFocus", "BringWindowToTop", "SwitchToThisWindow", "AttachThreadInput",
        "AllowSetForegroundWindow", "LockSetForegroundWindow", "ShowWindow", "SetWindowPlacement", "AppActivate",
        "System.Windows.Automation", "WScript.Shell", "CloseMainWindow",
        // R3: other ways to move/resize/reparent windows or change the display
        "MoveWindow", "DeferWindowPos", "BeginDeferWindowPos", "SetWindowLong", "SetParent", "EnableWindow",
        "SystemParametersInfo", "ChangeDisplaySettings", "SetThreadDesktop", "SwitchDesktop",
        // R5: process memory, injection, dumps, code loading
        "ReadProcessMemory", "WriteProcessMemory", "VirtualAllocEx", "CreateRemoteThread", "MiniDumpWriteDump",
        "MainModule", ".Modules", "FileVersionInfo", ".Kill(", "Assembly.Load", "AssemblyLoadContext",
        "NativeLibrary", "GetProcAddress", "GetDelegateForFunctionPointer", "delegate* unmanaged", "Type.GetType(",
        "System.Management.Automation", "PowerShell.Create", "Invoke-Expression", "ScriptBlock",
        // R5: network
        "HttpClient", "WebClient", "WebRequest", "System.Net.Sockets", "TcpClient", "UdpClient", "Dns.",
        // R5: registry writes (reads of HKCU\Software\TOS\TOS whitelist only)
        "Registry.SetValue", ".SetValue(", "CreateSubKey", "DeleteSubKey", "DeleteValue", "writable: true",
        "RegistryKeyPermissionCheck.ReadWriteSubTree",
        // UI: nothing that steals focus or blocks a thread on user input
        "MessageBox", "Console.ReadKey", "Console.ReadLine", "Interaction.InputBox",
        // R6: emulators / VMs
        "BlueStacks", "HD-Player", "adb.exe", "Hyper-V",
    ];

    /// <summary>
    /// Tokens allowed only in the listed projects (project folder name). Everywhere else they are forbidden.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RestrictedTokens =
        new Dictionary<string, IReadOnlyList<string>>
        {
            // Only the App starts a process: itself in --guardian mode (SPEC 10).
            ["Process.Start"] = ["Companion.App"],
            // Only GameIO talks to windows.
            ["PrintWindow"] = ["Companion.GameIO"],
            ["SetWindowPos"] = ["Companion.GameIO"],
            ["PostMessage"] = ["Companion.GameIO"],
            ["SendMessageTimeout"] = ["Companion.GameIO"],
            ["SendNotifyMessage"] = ["Companion.GameIO"],
            ["GetAsyncKeyState"] = ["Companion.GameIO"],
            ["Registry.CurrentUser"] = ["Companion.GameIO"],
            // Only the App registers the kill-switch hotkey.
            ["RegisterHotKey"] = ["Companion.App"],
            // Only Perception uses WinRT OCR.
            ["Windows.Media.Ocr"] = ["Companion.Perception"],
        };

    /// <summary>
    /// The only P/Invoke entry points ("dll!EntryPoint", dll lower-case without extension) allowed per project.
    /// DllImport and LibraryImport are both checked (EntryPoint if given, otherwise the method name). Projects not
    /// listed may not declare any P/Invoke.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PInvokeAllowlist =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["Companion.GameIO"] =
            [
                "user32!EnumWindows", "user32!GetWindowThreadProcessId", "user32!IsWindow", "user32!IsWindowVisible",
                "user32!IsIconic", "user32!IsZoomed", "user32!IsHungAppWindow", "user32!GetForegroundWindow",
                "user32!GetClassNameW", "user32!GetWindowRect", "user32!GetClientRect", "user32!GetWindowLongW",
                "user32!GetWindowLongPtrW", "user32!GetLayeredWindowAttributes", "user32!ClientToScreen", "user32!ScreenToClient",
                "user32!WindowFromPoint", "user32!GetAncestor", "user32!GetWindow", "user32!GetTopWindow",
                "user32!GetCursorPos", "user32!GetCursorInfo", "user32!GetClipCursor", "user32!GetLastInputInfo",
                "user32!GetAsyncKeyState", "user32!MonitorFromWindow", "user32!MonitorFromRect", "user32!MonitorFromPoint",
                "user32!GetMonitorInfoW", "user32!EnumDisplayMonitors", "user32!GetDpiForWindow", "user32!GetKeyboardLayout",
                "user32!GetSystemMetrics", "user32!OpenInputDesktop", "user32!CloseDesktop", "user32!GetGuiResources",
                "user32!PrintWindow", "user32!PostMessageW", "user32!SendMessageTimeoutW", "user32!SendNotifyMessageW",
                "user32!SetWindowPos",
                "shcore!GetDpiForMonitor",
                "dwmapi!DwmGetWindowAttribute",
                "kernel32!OpenProcess", "kernel32!QueryFullProcessImageNameW", "kernel32!CloseHandle",
                "kernel32!CreateWaitableTimerExW", "kernel32!SetWaitableTimer", "kernel32!SetWaitableTimerEx",
                "kernel32!WaitForSingleObject", "kernel32!GetCurrentProcess",
                "winmm!timeBeginPeriod", "winmm!timeEndPeriod",
            ],
            ["Companion.Logs"] = ["kernel32!GetFileInformationByHandle"],
            // Operator-run diagnostic (tools\Companion.CaptureProbe): a Direct3D device for one Windows.Graphics.Capture
            // session. Its window access (find, move, PrintWindow) goes through GameIO's internal layer.
            ["Companion.CaptureProbe"] = ["d3d11!D3D11CreateDevice", "d3d11!CreateDirect3D11DeviceFromDXGIDevice"],
            ["Companion.App"] = ["user32!RegisterHotKey", "user32!UnregisterHotKey"],
        };

    /// <summary>Runtime assert in GameIO: OpenProcess may only request PROCESS_QUERY_LIMITED_INFORMATION.</summary>
    public const uint OpenProcessAllowedAccess = 0x1000;

    /// <summary>Runtime assert in GameIO: GetAsyncKeyState only for VK_LBUTTON, VK_RBUTTON, VK_MBUTTON.</summary>
    public static readonly IReadOnlyList<int> GetAsyncKeyStateAllowedKeys = [0x01, 0x02, 0x04];

    /// <summary>
    /// Extra tokens forbidden in feature projects (features\**): features reach the game only through the engine
    /// contracts handed to them in <c>IJobContext</c> (FR-03).
    /// </summary>
    public static readonly IReadOnlyList<string> FeatureForbiddenTokens =
    [
        "IGameIo", "ILogTracker", "IPauseControl", "IStopSwitch", "DllImport", "LibraryImport", "File.", "Directory.",
        "FileStream", "FileInfo", "StreamWriter", "StreamReader", "Process.", "System.Diagnostics", "Thread(",
        "System.Reflection", "Activator.", "Environment.Exit", "Microsoft.Win32", "System.Windows.Forms", "System.Drawing",
        "Windows.Media", "AllowUnsafeBlocks",
    ];

    /// <summary>Project folders a feature project may reference (csproj ProjectReference check).</summary>
    public static readonly IReadOnlyList<string> FeatureAllowedReferences = ["Companion.Contracts"];

    /// <summary>Registry values (prefix match, HKCU\Software\TOS\TOS) GameIO may read; everything else is never read (KS-02).</summary>
    public static readonly IReadOnlyList<string> RegistryReadWhitelistPrefixes =
    [
        "Screenmanager Fullscreen mode", "Screenmanager Window Position X", "Screenmanager Window Position Y",
        "Screenmanager Resolution Width", "Screenmanager Resolution Height", "Resolution Window Width",
        "Resolution Window Height", "UnitySelectMonitor", "neo_log_save_openLog", "neo_log_save_openWrite",
    ];

    /// <summary>The game's registry key (read-only).</summary>
    public const string GameRegistryKey = @"Software\TOS\TOS";
}
