using System.Drawing.Drawing2D;
using Omaclip;

namespace ClipShare;

/// <summary>
/// The Spotlight-style history panel: type to search, ↑/↓ and Enter to choose, Ctrl+1–9 for quick picks,
/// Tab to switch between All / This PC / Peers. Choosing an entry copies it and (optionally) pastes it
/// into the app that was in front.
/// </summary>
internal sealed class HistoryForm : Form
{
    private enum Filter { All, Local, Remote }

    private const string IconFont = "Segoe MDL2 Assets";
    private const string SearchGlyph = "", LocalGlyph = "", RemoteGlyph = "", EmptyGlyph = "";

    private readonly AppModel model;
    private readonly TextBox search = new();
    private readonly Label searchIcon = new();
    private readonly SegmentedControl filterBar = new(["All", AppModel.LocalSourceName, "Peers"]);
    private readonly ListBox list = new();
    private readonly Label empty = new();
    private readonly Label hints = new();
    private readonly Label peers = new();
    private readonly Panel topDivider = new(), bottomDivider = new();
    private readonly Dictionary<string, Image?> thumbnails = [];
    private readonly Font textFont, captionFont, iconFont, smallIconFont;
    private Theme theme = Theme.Current;
    private List<ClipItem> visible = [];
    private Filter filter = Filter.All;
    private IntPtr pasteTarget;

    public HistoryForm(AppModel model)
    {
        this.model = model;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        Text = "ClipShare History";
        ClientSize = new Size(460, 540);
        MinimumSize = new Size(360, 320);
        Padding = new Padding(1);
        DoubleBuffered = true;

        textFont = new Font("Segoe UI", 10F);
        captionFont = new Font("Segoe UI", 8.25F);
        iconFont = new Font(IconFont, 11F);
        smallIconFont = new Font(IconFont, 7.5F);

        var searchRow = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 14, 14, 6) };
        searchIcon.Text = SearchGlyph;
        searchIcon.Font = iconFont;
        searchIcon.Dock = DockStyle.Left;
        searchIcon.Width = 26;
        searchIcon.TextAlign = ContentAlignment.MiddleLeft;
        search.BorderStyle = BorderStyle.None;
        search.Font = new Font("Segoe UI", 13F);
        search.PlaceholderText = "Search clipboard history";
        search.Dock = DockStyle.Fill;
        search.TextChanged += (_, _) => Refresh(resetSelection: true);
        searchRow.Controls.Add(search);
        searchRow.Controls.Add(searchIcon);

        var filterRow = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(14, 2, 14, 8) };
        filterBar.Dock = DockStyle.Fill;
        filterBar.SelectedChanged += index =>
        {
            filter = (Filter)index;
            Refresh(resetSelection: true);
            search.Focus();
        };
        filterRow.Controls.Add(filterBar);

        topDivider.Dock = DockStyle.Top;
        topDivider.Height = 1;
        bottomDivider.Dock = DockStyle.Bottom;
        bottomDivider.Height = 1;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(14, 0, 14, 0) };
        hints.Dock = DockStyle.Fill;
        hints.TextAlign = ContentAlignment.MiddleLeft;
        hints.Font = captionFont;
        hints.AutoEllipsis = true;
        peers.Dock = DockStyle.Right;
        peers.TextAlign = ContentAlignment.MiddleRight;
        peers.Font = captionFont;
        peers.AutoSize = false;
        peers.Width = 150;
        peers.AutoEllipsis = true;
        footer.Controls.Add(hints);
        footer.Controls.Add(peers);

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.DrawMode = DrawMode.OwnerDrawVariable;
        list.IntegralHeight = false;
        list.TabStop = false;
        list.MeasureItem += MeasureItem;
        list.DrawItem += DrawItem;
        list.MouseMove += (_, e) =>
        {
            var index = list.IndexFromPoint(e.Location);
            if (index >= 0 && index != list.SelectedIndex) list.SelectedIndex = index;
        };
        list.MouseClick += (_, e) =>
        {
            var index = list.IndexFromPoint(e.Location);
            if (index >= 0 && index < visible.Count) Choose(visible[index]);
        };
        list.GotFocus += (_, _) => search.Focus();

        empty.Dock = DockStyle.Fill;
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.Visible = false;

        Controls.Add(list);
        Controls.Add(empty);
        Controls.Add(topDivider);
        Controls.Add(filterRow);
        Controls.Add(searchRow);
        Controls.Add(bottomDivider);
        Controls.Add(footer);

        model.Changed += OnModelChanged;
        Deactivate += (_, _) => Hide();
        Resize += (_, _) => { if (Visible) Refresh(resetSelection: false); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: no Alt+Tab entry
            return parameters;
        }
    }

    public void Toggle()
    {
        if (Visible) Hide();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        pasteTarget = model.Foreground.PasteTarget();
        ApplyTheme();
        search.Text = "";
        filter = Filter.All;
        filterBar.Selected = 0;
        PositionOnMouseScreen();
        Refresh(resetSelection: true);
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        search.Focus();
    }

    private void PositionOnMouseScreen()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2 - area.Height / 10);
    }

    private void ApplyTheme()
    {
        theme = Theme.Current;
        BackColor = theme.Divider;
        foreach (Control control in Controls) Recolor(control);
        list.BackColor = theme.Background;
        search.BackColor = theme.Background;
        search.ForeColor = theme.Text;
        searchIcon.ForeColor = theme.SecondaryText;
        hints.ForeColor = peers.ForeColor = theme.SecondaryText;
        empty.ForeColor = theme.SecondaryText;
        topDivider.BackColor = bottomDivider.BackColor = theme.Divider;
        filterBar.Theme = theme;
        Native.SetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, theme.Dark ? 1 : 0);
    }

    private void Recolor(Control control)
    {
        if (control != topDivider && control != bottomDivider) control.BackColor = theme.Background;
        foreach (Control child in control.Controls) Recolor(child);
    }

    private void OnModelChanged()
    {
        if (Visible) Refresh(resetSelection: false);
    }

    private List<ClipItem> Filtered() => model.AllItems.Where(item =>
    {
        if (filter == Filter.Local && item.IsRemote) return false;
        if (filter == Filter.Remote && !item.IsRemote) return false;
        var query = search.Text;
        if (query.Length == 0) return true;
        return (item.Text ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.SourceName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }).ToList();

    private void Refresh(bool resetSelection)
    {
        var selectedId = !resetSelection && list.SelectedIndex >= 0 && list.SelectedIndex < visible.Count ? visible[list.SelectedIndex].Id : null;
        visible = Filtered();
        PruneThumbnails();

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var item in visible) list.Items.Add(item.Id);
        var index = selectedId is null ? 0 : Math.Max(visible.FindIndex(item => item.Id == selectedId), 0);
        if (visible.Count > 0) list.SelectedIndex = Math.Min(index, visible.Count - 1);
        list.EndUpdate();

        empty.Visible = visible.Count == 0;
        list.Visible = visible.Count > 0;
        empty.Text = search.Text.Length == 0 ? "Nothing copied yet" : "No matches";
        empty.Font = textFont;

        hints.Text = (model.Preferences.PasteAfterChoosing ? "↵ copy & paste" : "↵ copy") + "   Ctrl+1–9 quick pick   Tab filter   Esc close";
        peers.Text = model.PeerStatuses.Count == 0 ? "No peers" : string.Join(", ", model.PeerStatuses.Select(s => s.Peer.DisplayName));
    }

    private void Select(int index)
    {
        if (visible.Count == 0) return;
        list.SelectedIndex = Math.Clamp(index, 0, visible.Count - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var current = Math.Max(list.SelectedIndex, 0);
        switch (keyData)
        {
            case Keys.Escape:
                Hide();
                return true;
            case Keys.Down:
                Select(current + 1);
                return true;
            case Keys.Up:
                Select(current - 1);
                return true;
            case Keys.PageDown:
                Select(current + 5);
                return true;
            case Keys.PageUp:
                Select(current - 5);
                return true;
            case Keys.Enter:
                if (current < visible.Count) Choose(visible[current]);
                return true;
            case Keys.Tab:
            case Keys.Tab | Keys.Shift:
                var step = keyData.HasFlag(Keys.Shift) ? 2 : 1;
                filterBar.Selected = (filterBar.Selected + step) % 3;
                return true;
        }
        // Ctrl+1 … Ctrl+9 picks the nth visible item.
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == Keys.Control && key is >= Keys.D1 and <= Keys.D9 or >= Keys.NumPad1 and <= Keys.NumPad9)
        {
            var digit = key >= Keys.NumPad1 ? key - Keys.NumPad1 : key - Keys.D1;
            if (digit < visible.Count) Choose(visible[digit]);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Choose(ClipItem item)
    {
        model.CopyToClipboard(item);
        Hide();
        if (model.Preferences.PasteAfterChoosing) Paster.PasteToFrontApp(pasteTarget);
    }

    // MARK: Resizing and moving a borderless window

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != Native.WM_NCHITTEST || (int)m.Result != Native.HTCLIENT) return;
        var point = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
        var grip = LogicalToDeviceUnits(6);
        bool left = point.X < grip, right = point.X >= ClientSize.Width - grip, top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
        m.Result = (top, bottom, left, right) switch
        {
            (true, _, true, _) => Native.HTTOPLEFT,
            (true, _, _, true) => Native.HTTOPRIGHT,
            (_, true, true, _) => Native.HTBOTTOMLEFT,
            (_, true, _, true) => Native.HTBOTTOMRIGHT,
            (true, _, _, _) => Native.HTTOP,
            (_, true, _, _) => Native.HTBOTTOM,
            (_, _, true, _) => Native.HTLEFT,
            (_, _, _, true) => Native.HTRIGHT,
            _ => Native.HTCLIENT,
        };
    }

    // MARK: Rows

    private int Scale(int logical) => LogicalToDeviceUnits(logical);

    private Image? Thumbnail(ClipItem item)
    {
        var key = item.Id + ":" + item.Checksum;
        if (!thumbnails.TryGetValue(key, out var image))
        {
            image = ImageCodec.Decode(item.Payload);
            thumbnails[key] = image;
        }
        return image;
    }

    private void PruneThumbnails()
    {
        var keep = visible.Select(item => item.Id + ":" + item.Checksum).ToHashSet();
        foreach (var key in thumbnails.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            thumbnails[key]?.Dispose();
            thumbnails.Remove(key);
        }
    }

    /// <summary>Up to three lines of the item's text, each cut short if very long.</summary>
    private static string Preview(ClipItem item) =>
        string.Join("\n", (item.Text ?? "").Trim().Split('\n').Take(3)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.Length > 300 ? line[..300] : line));

    private int ContentWidth => list.ClientSize.Width - Scale(6 * 2 + 10 * 2) - Scale(46);

    private Size ContentSize(Graphics graphics, ClipItem item)
    {
        if (item.Kind == ContentKind.Image)
        {
            var image = Thumbnail(item);
            if (image is null) return new Size(ContentWidth, textFont.Height);
            var maxHeight = Scale(90);
            var scale = Math.Min(1.0, Math.Min((double)maxHeight / image.Height, (double)ContentWidth / image.Width));
            return new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        }
        var lineHeight = TextRenderer.MeasureText(graphics, "Ag", textFont).Height;
        var measured = TextRenderer.MeasureText(graphics, Preview(item), textFont, new Size(ContentWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        return new Size(ContentWidth, Math.Clamp(measured.Height, lineHeight, lineHeight * 3));
    }

    private void MeasureItem(object? sender, MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= visible.Count) return;
        var content = ContentSize(e.Graphics, visible[e.Index]);
        e.ItemHeight = Math.Min(255, Scale(8) + content.Height + Scale(4) + captionFont.Height + Scale(8) + Scale(2));
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        var graphics = e.Graphics;
        var bounds = e.Bounds;
        using (var background = new SolidBrush(theme.Background)) graphics.FillRectangle(background, bounds);
        if (e.Index < 0 || e.Index >= visible.Count) return;
        var item = visible[e.Index];
        var row = Rectangle.FromLTRB(bounds.Left + Scale(6), bounds.Top + Scale(1), bounds.Right - Scale(6), bounds.Bottom - Scale(1));

        if ((e.State & DrawItemState.Selected) != 0)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRectangle(row, Scale(8));
            using var brush = new SolidBrush(theme.Selection);
            graphics.FillPath(brush, path);
        }

        var left = row.Left + Scale(10);
        var top = row.Top + Scale(7);
        var content = ContentSize(graphics, item);
        if (item.Kind == ContentKind.Image && Thumbnail(item) is { } image)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(left, top, content.Width, content.Height));
        }
        else
        {
            var text = item.Kind == ContentKind.Image ? "Image" : Preview(item);
            TextRenderer.DrawText(graphics, text, textFont, new Rectangle(left, top, content.Width, content.Height), theme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        }

        var captionTop = top + content.Height + Scale(4);
        var glyph = item.IsRemote ? RemoteGlyph : LocalGlyph;
        var glyphSize = TextRenderer.MeasureText(graphics, glyph, smallIconFont);
        TextRenderer.DrawText(graphics, glyph, smallIconFont, new Point(left, captionTop + Scale(2)), theme.SecondaryText, TextFormatFlags.NoPadding);
        var caption = item.SourceName + "   " + RelativeTime(item.Date);
        TextRenderer.DrawText(graphics, caption, captionFont, new Point(left + glyphSize.Width + Scale(4), captionTop), theme.SecondaryText,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (e.Index < 9)
        {
            var badge = "Ctrl+" + (e.Index + 1);
            var size = TextRenderer.MeasureText(graphics, badge, captionFont);
            TextRenderer.DrawText(graphics, badge, captionFont, new Point(row.Right - Scale(10) - size.Width, row.Top + Scale(8)),
                theme.TertiaryText, TextFormatFlags.NoPrefix);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static string RelativeTime(DateTimeOffset date)
    {
        var elapsed = DateTimeOffset.Now - date;
        if (elapsed < TimeSpan.FromSeconds(5)) return "now";
        if (elapsed < TimeSpan.FromMinutes(1)) return $"{(int)elapsed.TotalSeconds} sec. ago";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min. ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} hr. ago";
        var days = (int)elapsed.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            model.Changed -= OnModelChanged;
            foreach (var image in thumbnails.Values) image?.Dispose();
            textFont.Dispose();
            captionFont.Dispose();
            iconFont.Dispose();
            smallIconFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A small segmented control (All / This PC / Peers), drawn to match the panel's theme.</summary>
internal sealed class SegmentedControl : Control
{
    private readonly string[] segments;
    private int selected;
    private Theme theme = ClipShare.Theme.Current;

    public event Action<int>? SelectedChanged;

    public SegmentedControl(string[] segments)
    {
        this.segments = segments;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Font = new Font("Segoe UI", 9F);
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => theme;
        set
        {
            theme = value;
            Invalidate();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => selected;
        set
        {
            if (value == selected) return;
            selected = value;
            Invalidate();
            SelectedChanged?.Invoke(value);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Selected = Math.Clamp(e.X * segments.Length / Math.Max(Width, 1), 0, segments.Length - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(theme.Background);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = LogicalToDeviceUnits(6);
        using (var track = Rounded(bounds, radius))
        using (var trackBrush = new SolidBrush(theme.Dark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(234, 234, 234)))
            graphics.FillPath(trackBrush, track);

        var width = (float)Width / segments.Length;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = Rectangle.Round(new RectangleF(i * width + 2, 2, width - 4, Height - 5));
            if (i == selected)
            {
                using var path = Rounded(segment, radius - 1);
                using var brush = new SolidBrush(theme.Dark ? Color.FromArgb(80, 80, 80) : Color.White);
                graphics.FillPath(brush, path);
            }
            TextRenderer.DrawText(graphics, segments[i], Font, segment, i == selected ? theme.Text : theme.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(radius * 2, 1);
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
