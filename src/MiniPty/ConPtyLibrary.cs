using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// An out-of-band ConPTY implementation, <c>conpty.dll</c> plus its console host <c>OpenConsole.exe</c>, supplied by
/// the application and selected per session through <see cref="PtyStartInfo.ConPty"/>. Windows only.
/// </summary>
/// <remarks>
/// <para>
/// Windows ships ConPTY in <c>kernel32.dll</c>, backed by whatever <c>conhost.exe</c> the Windows build carries, so a
/// terminal's VT behavior depends on the user's Windows version. Microsoft also publishes the implementation that
/// Windows Terminal carries, as the NuGet package <c>Microsoft.Windows.Console.ConPTY</c>: <c>conpty.dll</c> plus
/// <c>OpenConsole.exe</c>, one host process per session like the in-box one. MiniPty ships neither file and takes no
/// dependency on that package. An application that wants the out-of-band implementation references the package itself,
/// stages the two files in its output (MiniPty's build targets place <c>OpenConsole.exe</c> beside <c>conpty.dll</c>
/// when the package is referenced), loads them with <see cref="Load"/> or
/// <see cref="TryLoadFromApplicationDirectory"/>, and passes the result to <see cref="PtyStartInfo.ConPty"/>. Sessions
/// without it keep using the in-box ConPTY, so nothing changes for an application that does not opt in.
/// </para>
/// <para>
/// A library is loaded once per path and shared by every session that names it; the native module stays loaded for
/// the lifetime of the process. The contract with the DLL is its exports, not a version: <see cref="Load"/> resolves
/// the five functions MiniPty calls and fails if one is missing. <see cref="FileVersion"/> is reported for logs and
/// bug reports. MiniPty is verified against the package version named in its documentation.
/// </para>
/// <para>
/// <c>conpty.dll</c> launches <c>OpenConsole.exe</c> from beside itself, or from a subdirectory named after the operating
/// system's architecture (<c>x64</c>, <c>arm64</c>, <c>x86</c>), and when neither exists it launches the in-box
/// <c>conhost.exe</c> instead, without an error: the session works, on the implementation the package was taken to
/// avoid. <see cref="HasConsoleHost"/> reports that probe before the first spawn, so an application can detect a
/// missing host instead of discovering it from the terminal's behavior.
/// </para>
/// </remarks>
public sealed unsafe class ConPtyLibrary
{
    private const string LibraryFileName = "conpty.dll";
    private const string ConsoleHostFileName = "OpenConsole.exe";

    // One instance per path: the native module is reference counted by the loader, and a second load of the same file
    // would resolve the same functions again. Keyed case-insensitively, as Windows paths are.
    private static readonly Dictionary<string, ConPtyLibrary> s_loaded = new(StringComparer.OrdinalIgnoreCase);

    // HRESULT ConptyCreatePseudoConsole(COORD size, HANDLE hInput, HANDLE hOutput, DWORD dwFlags, HPCON* phPC)
    private readonly delegate* unmanaged[Stdcall]<WindowsCoord, nint, nint, uint, nint*, int> _create;
    // HRESULT ConptyResizePseudoConsole(HPCON hPC, COORD size)
    private readonly delegate* unmanaged[Stdcall]<nint, WindowsCoord, int> _resize;
    // HRESULT ConptyClearPseudoConsole(HPCON hPC, BOOL keepCursorRow). Two arguments in every published version; the
    // second is always passed explicitly, as a one-argument call would leave it to whatever the register holds.
    private readonly delegate* unmanaged[Stdcall]<nint, int, int> _clear;
    // HRESULT ConptyReleasePseudoConsole(HPCON hPC)
    private readonly delegate* unmanaged[Stdcall]<nint, int> _release;
    // void ConptyClosePseudoConsole(HPCON hPC)
    private readonly delegate* unmanaged[Stdcall]<nint, void> _close;

    private ConPtyLibrary(string path, nint module)
    {
        Path = path;
        _create = (delegate* unmanaged[Stdcall]<WindowsCoord, nint, nint, uint, nint*, int>)Export(module, path, "ConptyCreatePseudoConsole");
        _resize = (delegate* unmanaged[Stdcall]<nint, WindowsCoord, int>)Export(module, path, "ConptyResizePseudoConsole");
        _clear = (delegate* unmanaged[Stdcall]<nint, int, int>)Export(module, path, "ConptyClearPseudoConsole");
        _release = (delegate* unmanaged[Stdcall]<nint, int>)Export(module, path, "ConptyReleasePseudoConsole");
        _close = (delegate* unmanaged[Stdcall]<nint, void>)Export(module, path, "ConptyClosePseudoConsole");
        FileVersion = ReadFileVersion(path);
        ConsoleHostPath = ProbeConsoleHost(System.IO.Path.GetDirectoryName(path)!);
    }

    /// <summary>Gets the absolute path of the loaded <c>conpty.dll</c>.</summary>
    public string Path { get; }

    /// <summary>
    /// Gets the DLL's file version, from its version resource, for diagnostics. The package version
    /// <c>1.25.260930003</c>, for example, carries file version <c>1.25.2609.30003</c>.
    /// </summary>
    public Version FileVersion { get; }

    /// <summary>
    /// Gets the path of the <c>OpenConsole.exe</c> that <c>conpty.dll</c> will launch, or <see langword="null"/> when
    /// none is where it looks, in which case sessions silently run on the in-box <c>conhost.exe</c>.
    /// </summary>
    public string? ConsoleHostPath { get; }

    /// <summary>
    /// Gets a value indicating whether <c>OpenConsole.exe</c> is where <c>conpty.dll</c> looks for it: beside the DLL,
    /// or in a subdirectory named after the operating system's architecture.
    /// </summary>
    public bool HasConsoleHost => ConsoleHostPath is not null;

    /// <summary>
    /// Loads <c>conpty.dll</c> from an absolute path and resolves the functions MiniPty uses. Returns the same instance
    /// for the same path.
    /// </summary>
    /// <param name="path">Absolute path of <c>conpty.dll</c>. Never a bare file name: a name would be searched for
    /// along the loader's path and could bind a copy of the DLL that the operating system ships.</param>
    /// <returns>The loaded library, shared by every session that names it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not an absolute path.</exception>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not Windows.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="BadImageFormatException">The file is not a DLL for this process's architecture.</exception>
    /// <exception cref="EntryPointNotFoundException">The DLL lacks one of the ConPTY exports MiniPty calls.</exception>
    [SupportedOSPlatform("windows")]
    public static ConPtyLibrary Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path of conpty.dll must be absolute.", nameof(path));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("An out-of-band ConPTY applies to Windows only.");

        path = System.IO.Path.GetFullPath(path);
        lock (s_loaded)
        {
            if (s_loaded.TryGetValue(path, out var loaded))
                return loaded;

            if (!File.Exists(path))
                throw new FileNotFoundException("conpty.dll was not found.", path);

            var module = NativeLibrary.Load(path);
            ConPtyLibrary library;
            try
            {
                library = new ConPtyLibrary(path, module);
            }
            catch
            {
                // A DLL that is not an out-of-band ConPTY is not kept loaded.
                NativeLibrary.Free(module);
                throw;
            }

            s_loaded.Add(path, library);
            return library;
        }
    }

    /// <summary>
    /// Loads the <c>conpty.dll</c> staged in the application directory, or returns <see langword="null"/> when there is
    /// none or the current operating system is not Windows.
    /// </summary>
    /// <remarks>
    /// Looks in <see cref="AppContext.BaseDirectory"/>, where a runtime-specific build places the DLL, then in its
    /// <c>runtimes\win-&lt;architecture&gt;\native</c> subdirectory for this process's architecture, where a portable
    /// build keeps it. Nothing outside the application directory is considered.
    /// </remarks>
    /// <returns>The loaded library, or <see langword="null"/>.</returns>
    public static ConPtyLibrary? TryLoadFromApplicationDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var baseDirectory = AppContext.BaseDirectory;
        var flat = System.IO.Path.Combine(baseDirectory, LibraryFileName);
        if (File.Exists(flat))
            return Load(flat);

        var architecture = ArchitectureName(RuntimeInformation.ProcessArchitecture);
        if (architecture is null)
            return null;

        var portable = System.IO.Path.Combine(baseDirectory, "runtimes", "win-" + architecture, "native", LibraryFileName);
        return File.Exists(portable) ? Load(portable) : null;
    }

    internal int CreatePseudoConsole(WindowsCoord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole)
    {
        var inputPinned = false;
        var outputPinned = false;
        try
        {
            input.DangerousAddRef(ref inputPinned);
            output.DangerousAddRef(ref outputPinned);
            nint handle;
            var hr = _create(size, input.DangerousGetHandle(), output.DangerousGetHandle(), flags, &handle);
            pseudoConsole = hr < 0 ? 0 : handle;
            return hr;
        }
        finally
        {
            if (outputPinned)
                output.DangerousRelease();
            if (inputPinned)
                input.DangerousRelease();
        }
    }

    internal int ResizePseudoConsole(nint pseudoConsole, WindowsCoord size) => _resize(pseudoConsole, size);

    internal int ClearPseudoConsole(nint pseudoConsole, bool keepCursorRow) => _clear(pseudoConsole, keepCursorRow ? 1 : 0);

    internal int ReleasePseudoConsole(nint pseudoConsole) => _release(pseudoConsole);

    internal void ClosePseudoConsole(nint pseudoConsole) => _close(pseudoConsole);

    private static nint Export(nint module, string path, string name)
    {
        if (NativeLibrary.TryGetExport(module, name, out var address))
            return address;

        throw new EntryPointNotFoundException($"{path} does not export {name}; it is not an out-of-band ConPTY MiniPty can use.");
    }

    private static Version ReadFileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    /// <summary>
    /// The DLL's own probe order: the host beside it, then in the subdirectory named after the operating system's
    /// native architecture (that of the machine, not of this process), then nothing, which means the in-box host.
    /// </summary>
    private static string? ProbeConsoleHost(string directory)
    {
        var beside = System.IO.Path.Combine(directory, ConsoleHostFileName);
        if (File.Exists(beside))
            return beside;

        var architecture = ArchitectureName(RuntimeInformation.OSArchitecture);
        if (architecture is null)
            return null;

        var inSubdirectory = System.IO.Path.Combine(directory, architecture, ConsoleHostFileName);
        return File.Exists(inSubdirectory) ? inSubdirectory : null;
    }

    private static string? ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => null,
    };
}
