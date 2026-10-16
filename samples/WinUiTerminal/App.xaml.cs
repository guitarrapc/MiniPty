using Microsoft.UI.Xaml;

namespace WinUiTerminal;

/// <summary>
/// WinUI 3 host for a MiniPty session, written to show the threading model a GUI needs and nothing else. The XAML
/// here only loads the control styles; the generated <c>Main</c> installs the dispatcher
/// <see cref="SynchronizationContext"/> on the UI thread, so awaits in <see cref="TerminalWindow"/> resume there.
/// </summary>
/// <remarks>
/// Pass <c>--smoke</c> to run a command that exits at once and close the window when its output has been shown;
/// the process exit code reports whether the output arrived (0) or not (1).
/// </remarks>
public partial class TerminalApp : Application
{
    private TerminalWindow? _window;

    public TerminalApp()
    {
        InitializeComponent();
        // A WinUI exception is otherwise reported only as a stowed-exception crash code; a sample should say why.
        UnhandledException += static (_, e) =>
        {
            Environment.ExitCode = 2;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "WinUiTerminal.crash.txt"), e.Exception?.ToString() ?? e.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var smoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--smoke") >= 0;
        _window = new TerminalWindow(smoke);
        _window.Activate();
    }
}
