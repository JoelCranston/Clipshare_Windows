namespace ClipShare;

internal static class Program
{
    /// <summary>Unexpected errors are appended here (and shown once) instead of silently killing the tray app.</summary>
    public static readonly string ErrorLog =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShare", "errors.log");

    [STAThread]
    private static void Main()
    {
        // One copy per user session: a second launch just exits.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\ClipShare-omaclip-client", out var first);
        if (!first) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: true);

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // Follow the Windows light/dark setting for standard controls.
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        Application.Run(new TrayApplication());
    }

    private static bool reported;

    private static void Report(Exception? exception, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"{DateTimeOffset.Now:O} {(fatal ? "fatal" : "error")}: {exception}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (reported && !fatal) return;
        reported = true;
        MessageBox.Show($"ClipShare ran into a problem:\n\n{exception?.Message}\n\nDetails were saved to {ErrorLog}.",
            "ClipShare", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
