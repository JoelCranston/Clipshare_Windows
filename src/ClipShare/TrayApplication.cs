using System.Drawing.Imaging;

namespace ClipShare;

/// <summary>
/// The notification-area icon and its menu, which mirror the macOS menu bar item: show history, paste
/// the latest peer entry, per-peer status, auto-copy toggle, settings and quit. The icon dims while no
/// peer is connected. Left-clicking the icon opens the history panel.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly AppModel model;
    private readonly HistoryForm history;
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly Icon normalIcon, dimmedIcon;
    private SettingsForm? settings;
    private bool? showingConnected;

    public TrayApplication()
    {
        model = new AppModel();
        history = new HistoryForm(model);

        using (var stream = typeof(TrayApplication).Assembly.GetManifestResourceStream("ClipShare.ico")!)
            normalIcon = new Icon(stream, SystemInformation.SmallIconSize);
        dimmedIcon = Dim(normalIcon);

        menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) history.Toggle();
        };
        tray.Text = "ClipShare";
        UpdateIcon();
        tray.Visible = true;

        var preferences = model.Preferences;
        HotKeyCenter.Shared.Register("showHistory", preferences.ShowHistoryShortcut, history.Toggle);
        HotKeyCenter.Shared.Register("pasteLatest", preferences.PasteLatestShortcut, model.PasteLatestRemote);
        preferences.Changed += () =>
        {
            HotKeyCenter.Shared.Update("showHistory", preferences.ShowHistoryShortcut);
            HotKeyCenter.Shared.Update("pasteLatest", preferences.PasteLatestShortcut);
        };
        model.Changed += UpdateIcon;

        model.Start();
        if (!preferences.HasPassphrase) ShowSettings();
    }

    // MARK: Icon

    private static Icon Dim(Icon icon)
    {
        using var source = icon.ToBitmap();
        using var dimmed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(dimmed))
        {
            var matrix = new ColorMatrix([
                [0.3f, 0.3f, 0.3f, 0, 0],
                [0.59f, 0.59f, 0.59f, 0, 0],
                [0.11f, 0.11f, 0.11f, 0, 0],
                [0, 0, 0, 0.45f, 0],
                [0, 0, 0, 0, 1],
            ]);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel, attributes);
        }
        return Icon.FromHandle(dimmed.GetHicon());
    }

    private void UpdateIcon()
    {
        var connected = model.PeerStatuses.Where(s => s.Connected).Select(s => s.Peer.DisplayName).ToList();
        var isConnected = connected.Count > 0;
        if (showingConnected != isConnected)
        {
            tray.Icon = isConnected ? normalIcon : dimmedIcon;
            showingConnected = isConnected;
        }
        var text = isConnected ? "ClipShare — connected to " + string.Join(", ", connected) : "ClipShare — no peer connected";
        tray.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    // MARK: Menu

    private void BuildMenu()
    {
        menu.Items.Clear();
        var preferences = model.Preferences;

        var show = new ToolStripMenuItem("Show Clipboard History", null, (_, _) => history.ShowPanel())
        {
            ShortcutKeyDisplayString = preferences.ShowHistoryShortcut.DisplayString,
            Font = new Font(menu.Font, FontStyle.Bold),
        };
        menu.Items.Add(show);

        var latest = model.LatestRemoteItem;
        var paste = new ToolStripMenuItem(latest is null ? "Paste Latest Peer Entry" : $"Paste Latest from {latest.SourceName}", null,
            (_, _) => model.PasteLatestRemote())
        {
            ShortcutKeyDisplayString = preferences.PasteLatestShortcut.DisplayString,
            Enabled = latest is not null,
        };
        menu.Items.Add(paste);
        if (latest is not null) menu.Items.Add(new ToolStripMenuItem("    " + Preview(latest)) { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Peers") { Enabled = false });
        if (model.PeerStatuses.Count == 0)
        {
            var none = preferences.HasPassphrase ? "    None found yet" : "    Set a passphrase in Settings";
            menu.Items.Add(new ToolStripMenuItem(none) { Enabled = false });
        }
        foreach (var status in model.PeerStatuses)
        {
            var state = status.LastError is { } error ? "⚠ " + error : status.LastSync is null ? "connecting…" : "connected";
            menu.Items.Add(new ToolStripMenuItem($"    {status.Peer.DisplayName} — {state}") { Enabled = false });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Auto-Copy from Peers", null, (_, _) => preferences.AutoCopyRemote = !preferences.AutoCopyRemote)
        {
            Checked = preferences.AutoCopyRemote,
        });
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit ClipShare", null, (_, _) => ExitThread()));
    }

    private static string Preview(ClipItem item)
    {
        if (item.Text is not { } text) return "Image";
        var line = text.Trim().ReplaceLineEndings(" ");
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    private void ShowSettings()
    {
        if (settings is null || settings.IsDisposed)
        {
            settings = new SettingsForm(model);
            settings.FormClosed += (_, _) => settings = null;
        }
        settings.Show();
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        model.Dispose();
        HotKeyCenter.Shared.Dispose();
        history.Dispose();
        settings?.Dispose();
        tray.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}
