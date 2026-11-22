using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClipShare;

/// <summary>What the monitor captured from the system clipboard.</summary>
internal abstract record CapturedContent
{
    /// <summary>Text with Linux (LF) line endings, which is how omaclip and the Mac exchange it.</summary>
    public sealed record Text(string Value) : CapturedContent;
    public sealed record Png(byte[] Data) : CapturedContent;
}

/// <summary>
/// Listens for WM_CLIPBOARDUPDATE and reports new text or images. Also writes items back to the
/// clipboard without recording them as new copies.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    /// <summary>PNGs larger than this are not recorded (matches omaclip's default limit).</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico"];

    /// <summary>Formats password managers and other apps set to ask clipboard tools not to record an item.</summary>
    private static readonly string[] ExcludingFormats = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    public event Action<CapturedContent>? Captured;

    private uint ignoreUpTo;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Caption = "ClipShare clipboard", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    public void Start() => Native.AddClipboardFormatListener(Handle);

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE)
        {
            var sequence = Native.GetClipboardSequenceNumber();
            if (sequence > ignoreUpTo || ignoreUpTo - sequence > uint.MaxValue / 2)
            {
                if (Read() is { } content) Captured?.Invoke(content);
            }
            return;
        }
        base.WndProc(ref m);
    }

    private static T? Retry<T>(Func<T?> body) where T : class
    {
        // Another app may still hold the clipboard open right after it changes.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { return body(); }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return null;
    }

    private static CapturedContent? Read() => Retry(() =>
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return null;
        var formats = data.GetFormats(autoConvert: false);
        if (ExcludingFormats.Any(formats.Contains)) return null;
        if (formats.Contains("CanIncludeInClipboardHistory") && ReadDword(data, "CanIncludeInClipboardHistory") == 0) return null;

        // A copied image file in Explorer: share the image itself rather than its name.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            var file = files[0];
            if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && File.Exists(file) &&
                new FileInfo(file).Length <= 4 * MaxImageBytes)
            {
                var png = ImageCodec.ToPng(File.ReadAllBytes(file));
                return png is not null && png.Length <= MaxImageBytes ? new CapturedContent.Png(png) : null;
            }
        }
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } text)
        {
            return new CapturedContent.Text(text.ReplaceLineEndings("\n"));
        }
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream stream)
        {
            var png = ImageCodec.ToPng(stream.ToArray());
            return png is not null && png.Length <= MaxImageBytes ? new CapturedContent.Png(png) : null;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image image)
        {
            using (image)
            {
                var png = ImageCodec.Encode(image);
                return png.Length <= MaxImageBytes ? new CapturedContent.Png(png) : null;
            }
        }
        return (CapturedContent?)null;
    });

    private static int? ReadDword(IDataObject data, string format) =>
        data.GetData(format) is MemoryStream { Length: >= 4 } stream ? BitConverter.ToInt32(stream.ToArray(), 0) : null;

    /// <summary>Puts an item on the clipboard without recording it as a new local copy.</summary>
    public void Write(CapturedContent content)
    {
        var data = new DataObject();
        switch (content)
        {
            case CapturedContent.Text text:
                data.SetData(DataFormats.UnicodeText, text.Value.ReplaceLineEndings("\r\n"));
                break;
            case CapturedContent.Png png:
                data.SetData("PNG", false, new MemoryStream(png.Data));
                var image = ImageCodec.Decode(png.Data);
                if (image is not null) data.SetData(DataFormats.Bitmap, true, image);
                break;
        }
        try
        {
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);
        }
        catch (ExternalException ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClipShare: writing the clipboard failed: {ex.Message}");
        }
        ignoreUpTo = Native.GetClipboardSequenceNumber();
    }
}

internal static class ImageCodec
{
    public static Image? Decode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    public static byte[] Encode(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Returns PNG bytes unchanged, or converts other image formats to PNG.</summary>
    public static byte[]? ToPng(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return data;
        using var image = Decode(data);
        return image is null ? null : Encode(image);
    }
}
