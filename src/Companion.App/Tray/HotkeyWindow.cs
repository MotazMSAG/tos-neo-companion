using System.Runtime.InteropServices;

namespace Companion.App.Tray;

/// <summary>
/// Kill-switch hotkey Ctrl+Alt+Pause (SPEC 6.8) on a hidden message window. With Ctrl held, the Pause key reports
/// VK_CANCEL (Break) instead of VK_PAUSE, so both are registered. Only the App may register hotkeys (static rules).
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkCancel = 0x03;
    private const uint VkPause = 0x13;
    private const int IdPause = 0x5701;
    private const int IdBreak = 0x5702;

    private readonly Action _onHotkey;
    private bool _disposed;

    /// <summary>Creates the window and registers the hotkeys (UI thread).</summary>
    public HotkeyWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams { Caption = "TOS-companion.hotkey" });
        RegisteredPause = RegisterHotKey(Handle, IdPause, ModControl | ModAlt | ModNoRepeat, VkPause);
        RegisteredBreak = RegisterHotKey(Handle, IdBreak, ModControl | ModAlt | ModNoRepeat, VkCancel);
    }

    /// <summary>Ctrl+Alt+Pause registered.</summary>
    public bool RegisteredPause { get; }

    /// <summary>Ctrl+Alt+Break registered.</summary>
    public bool RegisteredBreak { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, IdPause);
            UnregisterHotKey(Handle, IdBreak);
            DestroyHandle();
        }
    }

    /// <inheritdoc />
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (m.WParam == IdPause || m.WParam == IdBreak))
        {
            try
            {
                _onHotkey();
            }
            catch (Exception)
            {
                // the stop path never throws into the message loop
            }

            return;
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
