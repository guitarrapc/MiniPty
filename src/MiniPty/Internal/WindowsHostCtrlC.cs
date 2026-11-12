using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MiniPty.Internal;

/// <summary>
/// Starts every Windows child with Ctrl+C processing on, even when the host process ignores Ctrl+C, so a Ctrl+C written
/// to the session interrupts the child as it would in a console window of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Windows keeps a per-process "ignore Ctrl+C" attribute (<c>SetConsoleCtrlHandler(NULL, TRUE)</c>, and the
/// implicit call for a process created with <c>CREATE_NEW_PROCESS_GROUP</c>), and <c>CreateProcess</c> copies it into
/// every child. No creation flag clears it: measured on build 26200, a child created with <c>CREATE_NEW_CONSOLE</c>,
/// <c>DETACHED_PROCESS</c>, <c>CREATE_NO_WINDOW</c>, or a pseudo-console inherits it, and a 32-bit child carries it in
/// both of its process parameter blocks. A host that ignores Ctrl+C is common: Git Bash (MSYS2) starts native programs
/// that way. ConPTY turns a written Ctrl+C into a <c>CTRL_C_EVENT</c> for the processes attached to the pseudo-console,
/// and such a child dropped it: a flooding child ran on for 30 s after the keystroke, on the in-box and the out-of-band
/// ConPTY alike, and so did Porta.Pty's children. With the attribute cleared the same child ended within about 2 ms.
/// </para>
/// <para>
/// <b>What.</b> While the host's attribute is set, <see cref="EnterStart"/> clears it with
/// <c>SetConsoleCtrlHandler(NULL, FALSE)</c> before <c>CreateProcess</c> and <see cref="ExitStart"/> sets it again
/// afterwards, so the child is created from a host that processes Ctrl+C. This is the Windows side of what the Unix shim
/// does when it resets every signal to its default action for the child: the child of a terminal starts with default
/// interrupt handling whatever its host chose for itself. The attribute governs only the events of a console the process
/// is attached to, and the child's pseudo-console is never the host's console, so clearing it for the child takes nothing
/// from the host.
/// </para>
/// <para>
/// <b>The window.</b> Starts share one window: the first start that finds the attribute set clears it, and the last start
/// to leave sets it again, so concurrent starts never undo each other and <c>CreateProcess</c> calls are not serialized.
/// Only entering and leaving take <see cref="s_lock"/>. While the window is open, a few milliseconds per start, the host
/// itself processes Ctrl+C: a Ctrl+C typed into the host's own console then reaches its handlers, and the default one ends
/// the host, and processes the host starts elsewhere meanwhile inherit the cleared attribute. A host that changes the
/// attribute itself while a session starts may find it set again afterwards. A host that never ignores Ctrl+C pays one
/// uncontended lock and one memory read per start.
/// </para>
/// <para>
/// <b>Reading the attribute.</b> Windows has no call that reports it. It is bit 0 of the <c>ConsoleFlags</c> field of the
/// process's <c>RTL_USER_PROCESS_PARAMETERS</c>, which <c>SetConsoleCtrlHandler</c> with a null handler sets and clears,
/// and which <c>CreateProcess</c> copies. <c>winternl.h</c> documents the path to that block,
/// <c>NtQueryInformationProcess</c> and <c>PEB.ProcessParameters</c>, but not the field, which sits in the block's
/// reserved pointers after the console handle, at 0x18 in a 64-bit process and 0x14 in a 32-bit one. The probe only
/// reads this process's own memory, once to find the field and then once per start. Where the field cannot be found,
/// starts leave the attribute alone, as before.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsHostCtrlC
{
    /// <summary>Bit 0 of <c>RTL_USER_PROCESS_PARAMETERS.ConsoleFlags</c>: the process ignores Ctrl+C.</summary>
    private const uint IgnoreCtrlC = 1;

    /// <summary>The pseudo-handle <c>GetCurrentProcess</c> returns.</summary>
    private const nint CurrentProcess = -1;

    private const int ProcessBasicInformationClass = 0;

    private static readonly Lock s_lock = new();

    // This process's ConsoleFlags, or null when it could not be found. Located once: the parameter block is set up with
    // the process and stays where it is.
    private static readonly uint* s_consoleFlags = LocateConsoleFlags();

    // Starts inside the window, and whether a start in it cleared the attribute, which the last one out sets again.
    // Guarded by s_lock.
    private static int s_starts;
    private static bool s_restore;

    /// <summary>Whether this process ignores Ctrl+C now; false where the attribute cannot be read.</summary>
    internal static bool HostIgnoresCtrlC => s_consoleFlags is not null && (Volatile.Read(ref *s_consoleFlags) & IgnoreCtrlC) != 0;

    /// <summary>
    /// Opens or joins the window in which this process processes Ctrl+C. Call right before <c>CreateProcess</c> and pair
    /// with <see cref="ExitStart"/> in a <c>finally</c>. Never throws.
    /// </summary>
    internal static void EnterStart()
    {
        lock (s_lock)
        {
            s_starts++;
            // Checked on every entry, not only the first: host code may have set the attribute while the window was
            // open.
            if (HostIgnoresCtrlC && SetConsoleCtrlHandler(IntPtr.Zero, false))
                s_restore = true;
        }
    }

    /// <summary>Leaves the window; the last start out sets the attribute again if a start in the window cleared it.</summary>
    internal static void ExitStart()
    {
        lock (s_lock)
        {
            if (--s_starts != 0 || !s_restore)
                return;

            s_restore = false;
            // Nothing to do on failure: the host then goes on processing Ctrl+C, as a process without the attribute does.
            _ = SetConsoleCtrlHandler(IntPtr.Zero, true);
        }
    }

    private static uint* LocateConsoleFlags()
    {
        try
        {
            ProcessBasicInformation information;
            if (NtQueryInformationProcess(CurrentProcess, ProcessBasicInformationClass, &information, sizeof(ProcessBasicInformation), null) != 0
                || information.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            // PEB.ProcessParameters follows 4 bytes of flags and two pointers; ConsoleFlags follows 16 bytes of lengths and
            // flags and the console handle in the parameter block.
            var processParametersOffset = IntPtr.Size == 8 ? 0x20 : 0x10;
            var consoleFlagsOffset = 16 + IntPtr.Size;
            var parameters = *(IntPtr*)(information.PebBaseAddress + processParametersOffset);
            return parameters == IntPtr.Zero ? null : (uint*)(parameters + consoleFlagsOffset);
        }
        catch (Exception)
        {
            // ntdll without the export, which no supported Windows lacks: leave the attribute alone.
            return null;
        }
    }

    /// <summary><c>PROCESS_BASIC_INFORMATION</c> from <c>winternl.h</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        void* processInformation,
        int processInformationLength,
        int* returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);
}
