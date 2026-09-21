using Avalonia;
using StarSimCore.Interop;

namespace StarSimCore.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                DiagnosticService.Current.RecordManagedException("AppDomain.UnhandledException", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            DiagnosticService.Current.RecordManagedException("TaskScheduler.UnobservedTaskException", eventArgs.Exception);
        App.StartInExpertMode = args.Any(
            argument => string.Equals(argument, "--expert", StringComparison.OrdinalIgnoreCase));
        App.EnableExternalPlugins = args.Any(
            argument => string.Equals(argument, "--enable-plugins", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(
                Environment.GetEnvironmentVariable("STARSIMCORE_ENABLE_PLUGINS"),
                "1",
                StringComparison.Ordinal);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
