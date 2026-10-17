using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AvaloniaTerminal;

/// <summary>
/// Entry point of a code-only Avalonia app: no XAML anywhere. Avalonia installs its synchronization context on
/// the UI thread, so the awaits in <see cref="TerminalWindow"/> resume there, exactly as in the WinUI sample.
/// </summary>
/// <remarks>
/// Arguments: <c>--smoke</c> runs a command that exits at once and quits with 0 once its output is on screen;
/// <c>--dump &lt;file&gt;</c> writes the status line and the visible screen as text when the program exits;
/// <c>--screenshot &lt;file.png&gt;</c> renders the window to a PNG at the same moment; anything else is the
/// program to run, with its arguments. The default is the user's shell.
/// </remarks>
internal static class Program
{
    internal static bool Smoke { get; private set; }

    internal static string? DumpPath { get; private set; }

    internal static string? ScreenshotPath { get; private set; }

    internal static string[] Command { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--smoke")
            {
                Smoke = true;
                continue;
            }

            if (args[i] == "--dump" && i + 1 < args.Length)
            {
                DumpPath = args[++i];
                continue;
            }

            if (args[i] == "--screenshot" && i + 1 < args.Length)
            {
                ScreenshotPath = args[++i];
                continue;
            }

            rest.Add(args[i]);
        }

        Command = rest.ToArray();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        // What App.axaml would declare: the control theme, in its dark variant to match the terminal colors.
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new TerminalWindow(desktop, Program.Smoke, Program.Command, Program.DumpPath, Program.ScreenshotPath);

        base.OnFrameworkInitializationCompleted();
    }
}
