using Microsoft.Win32;

namespace ClipShare;

/// <summary>Colors for the history panel, following the Windows light/dark app setting.</summary>
internal sealed record Theme(
    bool Dark,
    Color Background,
    Color Surface,
    Color Text,
    Color SecondaryText,
    Color TertiaryText,
    Color Selection,
    Color Divider,
    Color Accent)
{
    public static bool SystemUsesDarkMode
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
    }

    /// <summary>The user's Windows accent color, or Windows blue.</summary>
    private static Color AccentColor
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            return Color.FromArgb(0, 103, 192);
        }
    }

    public static Theme Current
    {
        get
        {
            var accent = AccentColor;
            return SystemUsesDarkMode
                ? new Theme(true, Color.FromArgb(32, 32, 32), Color.FromArgb(45, 45, 45), Color.FromArgb(243, 243, 243),
                    Color.FromArgb(170, 170, 170), Color.FromArgb(120, 120, 120), Blend(accent, Color.FromArgb(32, 32, 32), 0.35),
                    Color.FromArgb(60, 60, 60), accent)
                : new Theme(false, Color.FromArgb(249, 249, 249), Color.White, Color.FromArgb(27, 27, 27),
                    Color.FromArgb(96, 96, 96), Color.FromArgb(150, 150, 150), Blend(accent, Color.FromArgb(249, 249, 249), 0.22),
                    Color.FromArgb(229, 229, 229), accent);
        }
    }

    private static Color Blend(Color top, Color bottom, double amount) => Color.FromArgb(
        (int)(top.R * amount + bottom.R * (1 - amount)),
        (int)(top.G * amount + bottom.G * (1 - amount)),
        (int)(top.B * amount + bottom.B * (1 - amount)));
}
