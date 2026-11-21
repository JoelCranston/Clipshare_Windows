using System.Diagnostics;

namespace ClipShare;

/// <summary>
/// Sends Ctrl+V to another app. Windows needs no permission for this, except that apps running as
/// administrator don't accept input from a normal app.
/// </summary>
internal static class Paster
{
    private static readonly int[] Modifiers = [Native.VK_SHIFT, Native.VK_CONTROL, Native.VK_MENU, Native.VK_LWIN, Native.VK_RWIN];

    private static bool Held(int vk) => Native.GetAsyncKeyState(vk) < 0;

    /// <summary>
    /// Called when a shortcut fires: if Alt or Win is down, taps an unassigned key so their release
    /// isn't taken as a lone Alt (menu bar) or Win (Start) press.
    /// </summary>
    public static void MaskModifierRelease()
    {
        if (Held(Native.VK_MENU) || Held(Native.VK_LWIN) || Held(Native.VK_RWIN))
            Native.Send(Native.Key(Native.VK_MASK, up: false), Native.Key(Native.VK_MASK, up: true));
    }

    /// <summary>
    /// Brings <paramref name="target"/> (if given) to the front and posts Ctrl+V once the user has let go
    /// of the shortcut's modifier keys (up to a second), so apps don't see e.g. Win+Ctrl+Alt+V.
    /// VK_V is the key that types "v" in the current layout, so this works on Dvorak and friends.
    /// </summary>
    public static async void PasteToFrontApp(IntPtr target = default)
    {
        if (target != IntPtr.Zero && Native.IsWindow(target)) Native.SetForegroundWindow(target);
        var watch = Stopwatch.StartNew();
        await Task.Delay(50);
        while (Modifiers.Any(Held) && watch.ElapsedMilliseconds < 1000) await Task.Delay(20);
        Native.Send(
            Native.Key(Native.VK_CONTROL, up: false),
            Native.Key(Native.VK_V, up: false),
            Native.Key(Native.VK_V, up: true),
            Native.Key(Native.VK_CONTROL, up: true));
    }
}

/// <summary>
/// Remembers the last window of another app that was in front, so choosing from the history panel
/// (opened from the tray icon, say) pastes back into the app the user was working in.
/// </summary>
internal sealed class ForegroundTracker : IDisposable
{
    private static readonly HashSet<string> ShellClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
         "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "Progman", "WorkerW"];

    private readonly Native.WinEventProc callback;
    private readonly IntPtr hook;
    private readonly uint ownProcess = (uint)Environment.ProcessId;

    public IntPtr LastExternalWindow { get; private set; }

    public ForegroundTracker()
    {
        callback = OnForegroundChanged;
        hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT);
        Consider(Native.GetForegroundWindow());
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
        Consider(hwnd);

    private void Consider(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out var process);
        if (process == ownProcess || ShellClasses.Contains(Native.ClassName(hwnd))) return;
        LastExternalWindow = hwnd;
    }

    /// <summary>The window to paste into: the current foreground window unless it is ours or the shell.</summary>
    public IntPtr PasteTarget()
    {
        Consider(Native.GetForegroundWindow());
        return LastExternalWindow;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
    }
}
