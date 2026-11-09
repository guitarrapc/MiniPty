using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniPty.Internal;

/// <summary>
/// Observer of the exits of the ConPTY children this load of MiniPty starts: one Win32 thread-pool wait per child
/// (<c>SetThreadpoolWait</c>), so observing exit adds no thread per session and never occupies a .NET thread-pool
/// worker.
/// </summary>
/// <remarks>
/// Each watched child is tracked through a duplicate of its process handle that the watcher owns and closes when the
/// child exits. The session can therefore close its own handle at any time without unregistering first. A disposed
/// session's child is killed, so its wait always completes. The reasons and measurements are in Child exit
/// observation, <c>.github/docs/references/pty_crossplatform.md</c>.
/// </remarks>
internal static unsafe partial class WindowsExitWatcher
{
    private const uint DuplicateSameAccess = 0x00000002;

    /// <summary>
    /// Starts watching <paramref name="processHandle"/>. <paramref name="onExited"/> runs on a native thread-pool
    /// worker once the process exits. The caller keeps ownership of its own handle. A thread's handle signals once too,
    /// as the thread ends; the thread that closes a legacy pseudo-console is watched that way.
    /// </summary>
    internal static void Register(IntPtr processHandle, IWindowsExitObserver onExited)
    {
        var currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(currentProcess, processHandle, currentProcess, out var duplicate, 0, false, DuplicateSameAccess))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateHandle failed");

        var context = (WaitContext*)NativeMemory.Alloc((nuint)sizeof(WaitContext));
        context->Observer = GCHandle.ToIntPtr(GCHandle.Alloc(onExited));
        context->Process = duplicate;

        var wait = CreateThreadpoolWait(&OnProcessSignaled, (IntPtr)context, IntPtr.Zero);
        if (wait == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            Release(context);
            throw new Win32Exception(error, "CreateThreadpoolWait failed");
        }

        // A null timeout waits for as long as it takes. The wait reports once, and its callback closes it.
        SetThreadpoolWait(wait, duplicate, IntPtr.Zero);
    }

    /// <summary>
    /// The wait's callback. With no timeout, a process handle reports only its exit, so <paramref name="waitResult"/>
    /// is always <c>WAIT_OBJECT_0</c>. The observer reads the exit itself.
    /// </summary>
    /// <remarks>
    /// An exception that left this method would end the process. The observer only records exit and does not throw.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnProcessSignaled(IntPtr instance, IntPtr context, IntPtr wait, uint waitResult)
    {
        var state = (WaitContext*)context;
        var observer = (IWindowsExitObserver)GCHandle.FromIntPtr(state->Observer).Target!;
        Release(state);

        // A wait may be closed from its own callback: the pool frees it once the callback returns.
        CloseThreadpoolWait(wait);
        observer.OnProcessExited();
    }

    private static void Release(WaitContext* context)
    {
        GCHandle.FromIntPtr(context->Observer).Free();
        CloseHandle(context->Process);
        NativeMemory.Free(context);
    }

    /// <summary>What a wait's callback needs: the observer and the duplicate it closes.</summary>
    private struct WaitContext
    {
        public IntPtr Observer;
        public IntPtr Process;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateThreadpoolWait(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void> pfnwa,
        IntPtr pv,
        IntPtr pcbe);

    [LibraryImport("kernel32.dll")]
    private static partial void SetThreadpoolWait(IntPtr pwa, IntPtr h, IntPtr pftTimeout);

    [LibraryImport("kernel32.dll")]
    private static partial void CloseThreadpoolWait(IntPtr pwa);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}

/// <summary>Receives a child exit from <see cref="WindowsExitWatcher"/> on a native thread-pool worker.</summary>
internal interface IWindowsExitObserver
{
    void OnProcessExited();
}
