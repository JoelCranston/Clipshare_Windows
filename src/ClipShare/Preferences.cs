using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClipShare;

/// <summary>
/// User settings, saved as JSON in %APPDATA%\ClipShare\settings.json. The passphrase lives in
/// Windows Credential Manager, not in that file.
/// </summary>
internal sealed class Preferences
{
    private sealed class Stored
    {
        [JsonPropertyName("autoCopyRemote")] public bool AutoCopyRemote { get; set; }
        [JsonPropertyName("pasteAfterChoosing")] public bool PasteAfterChoosing { get; set; } = true;
        [JsonPropertyName("syncPort")] public int SyncPort { get; set; }
        [JsonPropertyName("manualPeers")] public string ManualPeers { get; set; } = "";
        [JsonPropertyName("maxHistory")] public int MaxHistory { get; set; } = 50;
        [JsonPropertyName("sharedCount")] public int SharedCount { get; set; } = 5;
        [JsonPropertyName("showHistoryShortcut")] public Shortcut? ShowHistoryShortcut { get; set; }
        [JsonPropertyName("pasteLatestShortcut")] public Shortcut? PasteLatestShortcut { get; set; }
    }

    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipShare");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private const string CredentialTarget = "ClipShare/omaclip-passphrase";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClipShare";

    private readonly Stored stored;

    /// <summary>Raised after any setting changes.</summary>
    public event Action? Changed;

    public Preferences()
    {
        try
        {
            stored = File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) ?? new Stored() : new Stored();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            stored = new Stored();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"ClipShare: saving settings failed: {ex.Message}");
        }
        Changed?.Invoke();
    }

    public bool AutoCopyRemote { get => stored.AutoCopyRemote; set { stored.AutoCopyRemote = value; Save(); } }
    public bool PasteAfterChoosing { get => stored.PasteAfterChoosing; set { stored.PasteAfterChoosing = value; Save(); } }
    public int SyncPort { get => stored.SyncPort; set { stored.SyncPort = value; Save(); } }
    public string ManualPeers { get => stored.ManualPeers; set { stored.ManualPeers = value; Save(); } }
    public int MaxHistory { get => Math.Clamp(stored.MaxHistory, 5, 500); set { stored.MaxHistory = value; Save(); } }
    public int SharedCount { get => Math.Clamp(stored.SharedCount, 0, 50); set { stored.SharedCount = value; Save(); } }

    public Shortcut ShowHistoryShortcut
    {
        get => stored.ShowHistoryShortcut ?? Shortcut.ShowHistoryDefault;
        set { stored.ShowHistoryShortcut = value; Save(); }
    }

    public Shortcut PasteLatestShortcut
    {
        get => stored.PasteLatestShortcut ?? Shortcut.PasteLatestDefault;
        set { stored.PasteLatestShortcut = value; Save(); }
    }

    public bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } path) key.SetValue(RunValue, $"\"{path}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            Changed?.Invoke();
        }
    }

    /// <summary>Keeps the Run entry pointing at this copy of the app if the user moved it.</summary>
    public void RefreshLaunchAtLoginPath()
    {
        if (LaunchAtLogin) LaunchAtLogin = true;
    }

    // MARK: Passphrase (Credential Manager)

    public string? Passphrase => Native.ReadCredential(CredentialTarget);

    public bool HasPassphrase => Passphrase is not null;

    public void SetPassphrase(string value)
    {
        if (!Native.WriteCredential(CredentialTarget, "omaclip", value))
            System.Diagnostics.Debug.WriteLine("ClipShare: saving the passphrase failed");
        Changed?.Invoke();
    }

    /// <summary>The passphrase from an existing omaclip install (<c>~/.config/omaclip/config.json</c>), if any.</summary>
    public static string? OmaclipConfigPassphrase()
    {
        var homes = new[] { Environment.GetEnvironmentVariable("HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var home in homes.Where(h => !string.IsNullOrEmpty(h)).Distinct())
        {
            var path = Path.Combine(home!, ".config", "omaclip", "config.json");
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("passphrase", out var value) && value.GetString() is { Length: > 0 } passphrase)
                    return passphrase;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }
        return null;
    }
}
