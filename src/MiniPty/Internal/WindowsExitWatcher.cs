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
/// <para>
/// A thread-pool wait holds no thread while it waits. With 2,000 waits pending the process gained no thread (Windows 11,
/// build 26200), where threads that each wait on 63 handles with <c>WaitForMultipleObjects</c>, this watcher's earlier
/// design and also how .NET's own <c>RegisterWaitForSingleObject</c> waits, gained 32. That is how the Windows thread
/// pool has waited since Windows 8, not a documented guarantee; a pool that waited on threads of its own would cost what
/// the earlier design did. The time from the child's exit to the callback measured the same in both designs: about 7 μs
/// at the median and 20 μs at the 99th percentile.
/// </para>
/// <para>
/// The callback runs on a worker of the process's native thread pool, not on .NET's. It only records exit
/// (<see cref="IWindowsExitObserver"/>), so it holds that worker for microseconds.
/// </para>
/// <para>
/// Each watched child is tracked through a duplicate of its process handle that the watcher owns and closes when the
/// child exits. The session can therefore close its own handle at any time without unregistering first: closing a
/// handle that a wait is pending on is undefined behavior, and the duplicate rules that out. A disposed session's child
/// is killed, so its wait always completes.
/// </para>
/// <para>
/// Registration allocates nothing on the managed heap: the observer travels to the callback as a
/// <see cref="GCHandle"/> in a two-pointer native block. <c>RegisterWaitForSingleObject</c> was measured at roughly
/// 270 bytes per session (wait handle wrappers, the registration object, its lock, a callback wrapper, and a completion
/// work item).
/// </para>
/// </remarks>
internal static unsafe partial class WindowsExitWatcher
{
    private const uint DuplicateSameAccess = 0x00000002;

    /// <summary>
    /// Starts watching <paramref name="processHandle"/>; <paramref name="onExited"/> runs on a native thread-pool
    /// worker once the process exits. The caller keeps ownership of its own handle.
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
    /// is always <c>WAIT_OBJECT_0</c>; the observer reads the exit itself.
    /// </summary>
    /// <remarks>
    /// An exception that left this method would end the process, as one that left the earlier design's watcher thread
    /// did. The observer only records exit and does not throw.
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
