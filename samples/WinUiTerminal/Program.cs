#pragma warning disable CA1806 // The Application instance registers itself with the XAML runtime; the reference is not needed.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace WinUiTerminal;

/// <summary>
/// Entry point of a code-only WinUI 3 app: no XAML file anywhere, so <c>Main</c> is written here rather than
/// generated. It does what the XAML compiler's <c>Main</c> would: initialize COM wrappers, start the XAML
/// application, and install the dispatcher <see cref="SynchronizationContext"/> on the UI thread so awaits in
/// <see cref="TerminalWindow"/> resume there.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var smoke = Array.IndexOf(args, "--smoke") >= 0;
        // --dump <file> writes the visible screen as text when the program exits, for scripted checks.
        string? dumpPath = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--smoke")
                continue;
            if (args[i] == "--dump" && i + 1 < args.Length)
            {
                dumpPath = args[++i];
                continue;
            }

            rest.Add(args[i]);
        }

        // Anything else on the command line is the program to run in the terminal; the default is the shell.
        var command = rest.ToArray();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new TerminalApp(smoke, command, dumpPath);
        });

        return Environment.ExitCode;
    }
}

/// <summary>
/// WinUI 3 host for a MiniPty session, written to show the threading model a GUI needs and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Two things replace what <c>App.xaml</c> would otherwise provide, and the app crashes with the stowed-exception
/// code <c>0xC000027B</c> if either is missing:
/// </para>
/// <list type="number">
/// <item>The application implements <see cref="IXamlMetadataProvider"/>. The XAML compiler generates this from an
/// <c>App.xaml</c>; without it the XAML runtime cannot resolve the control types used by WinUI's own resource
/// dictionaries, and <c>new XamlControlsResources()</c> fails with "Cannot find a resource with the given key:
/// AcrylicBackgroundFillColorDefaultBrush". This app defines no XAML types of its own, so it delegates to
/// <see cref="XamlControlsXamlMetaDataProvider"/>, which the generated provider also falls back to.</item>
/// <item><see cref="XamlControlsResources"/> is merged in <see cref="OnLaunched"/>, not in the constructor:
/// <see cref="Application.Resources"/> is not readable until the constructor has returned (E_UNEXPECTED).
/// <c>LoadComponent</c> from a generated <c>InitializeComponent</c> runs in the constructor too, but it sets the
/// resources through the XAML parser rather than through the property.</item>
/// </list>
/// <para>
/// Pass <c>--smoke</c> to run a command that exits at once and close the window when its output has been shown;
/// the process exit code reports whether the output arrived (0), did not (1), or the app faulted (2).
/// </para>
/// </remarks>
internal sealed class TerminalApp : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _controlsMetadata = new XamlControlsXamlMetaDataProvider();
    private readonly bool _smoke;
    private readonly string[] _command;
    private readonly string? _dumpPath;
    private TerminalWindow? _window;

    public TerminalApp(bool smoke, string[] command, string? dumpPath)
    {
        _smoke = smoke;
        _command = command;
        _dumpPath = dumpPath;
        // A WinUI exception is otherwise reported only as a stowed-exception crash code; a sample should say why.
        UnhandledException += static (_, e) =>
        {
            Environment.ExitCode = 2;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "WinUiTerminal.crash.txt"), e.Exception?.ToString() ?? e.Message);
        };
    }

    public IXamlType GetXamlType(Type type) => _controlsMetadata.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _controlsMetadata.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _controlsMetadata.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The default styles of every control, which App.xaml normally declares.
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        _window = new TerminalWindow(_smoke, _command, _dumpPath);
        _window.Activate();
    }
}
