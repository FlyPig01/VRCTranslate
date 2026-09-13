using Microsoft.UI.Xaml;

namespace VrcTranslate.Desktop;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public AppState State { get; } = new();

    public App()
    {
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            WriteStartupError(exception);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteStartupError(args.Exception);
    }

    private static void WriteStartupError(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "VrcTranslate-startup.log");
            File.WriteAllText(path, $"{DateTimeOffset.Now:O}\r\nHResult: 0x{exception.HResult:X8}\r\nType: {exception.GetType().FullName}\r\n{exception}\r\nInner: {exception.InnerException}\r\n");
        }
        catch
        {
            // Diagnostics must never mask the original startup failure.
        }
    }
}
