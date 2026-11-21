using System.Text.Json.Serialization;

namespace ClipShare;

/// <summary>
/// A global keyboard shortcut: a Windows virtual-key code plus MOD_* flags.
///
/// On Windows the virtual-key code of a letter follows the active keyboard layout (on Dvorak, the key
/// that types "v" sends VK_V), so storing the virtual key is the same as storing the character, the way
/// the macOS client does, and shortcuts keep working when the layout changes.
/// </summary>
public sealed record Shortcut(
    [property: JsonPropertyName("key")] int Key,
    [property: JsonPropertyName("modifiers")] int Modifiers)
{
    /// <summary>Win+Alt+V, next to Windows' own Win+V clipboard history (the Mac uses ⌥⌘V).</summary>
    public static readonly Shortcut ShowHistoryDefault = new(Native.VK_V, Native.MOD_WIN | Native.MOD_ALT);

    /// <summary>Win+Ctrl+Alt+V (the Mac uses ⌃⌥⌘V).</summary>
    public static readonly Shortcut PasteLatestDefault = new(Native.VK_V, Native.MOD_WIN | Native.MOD_CONTROL | Native.MOD_ALT);

    private static bool IsFunctionKey(Keys key) => key is >= Keys.F1 and <= Keys.F24;

    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
            if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            parts.Add(KeyName((Keys)Key));
            return string.Join("+", parts);
        }
    }

    /// <summary>The key's label under the current layout, e.g. "V", "F5", "Space", "/".</summary>
    public static string KeyName(Keys key)
    {
        switch (key)
        {
            case >= Keys.A and <= Keys.Z:
            case >= Keys.D0 and <= Keys.D9:
                return ((char)key).ToString();
            case Keys.Space: return "Space";
            case Keys.Return: return "Enter";
            case Keys.Tab: return "Tab";
            case Keys.Back: return "Backspace";
            case Keys.Delete: return "Delete";
            case Keys.Insert: return "Insert";
            case Keys.Home: return "Home";
            case Keys.End: return "End";
            case Keys.PageUp: return "PgUp";
            case Keys.PageDown: return "PgDn";
            case Keys.Left: return "←";
            case Keys.Right: return "→";
            case Keys.Up: return "↑";
            case Keys.Down: return "↓";
        }
        if (IsFunctionKey(key)) return "F" + (key - Keys.F1 + 1);
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9) return "Num" + (key - Keys.NumPad0);
        var character = Native.MapVirtualKeyW((uint)key, Native.MAPVK_VK_TO_CHAR) & 0x7FFF;
        return character > 0x20 ? char.ToUpperInvariant((char)character).ToString() : key.ToString();
    }

    /// <summary>Currently held Win key (WinForms doesn't report it in <see cref="Control.ModifierKeys"/>).</summary>
    public static bool WinHeld => Native.GetKeyState(Native.VK_LWIN) < 0 || Native.GetKeyState(Native.VK_RWIN) < 0;

    /// <summary>Builds a shortcut from a key press, or null if it isn't usable as a global shortcut.</summary>
    public static Shortcut? FromKeyPress(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
            return null;
        var modifiers = 0;
        if ((keyData & Keys.Control) != 0) modifiers |= Native.MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) modifiers |= Native.MOD_ALT;
        if ((keyData & Keys.Shift) != 0) modifiers |= Native.MOD_SHIFT;
        if (WinHeld) modifiers |= Native.MOD_WIN;
        // Require Win, Ctrl or Alt so ordinary typing isn't captured; F-keys may be used alone.
        var hasCommandModifier = (modifiers & (Native.MOD_WIN | Native.MOD_CONTROL | Native.MOD_ALT)) != 0;
        if (!hasCommandModifier && !IsFunctionKey(key)) return null;
        return new Shortcut((int)key, modifiers);
    }
}
