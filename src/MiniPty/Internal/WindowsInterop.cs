using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

internal static partial class WindowsInterop
{
    internal const uint WaitTimeout = 0x00000102;
    internal const uint WaitFailed = 0xFFFFFFFF;
    internal const int ErrorNoData = 232;

    /// <summary>
    /// Overlapped read of a handle the caller has pinned: pass a null byte count and read the count from the overlapped
    /// structure.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        IntPtr lpNumberOfBytesRead,
        NativeOverlapped* lpOverlapped);

    /// <summary>For a handle the caller has already pinned. A null overlapped pointer cancels all of this process's I/O on it.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    /// <summary>For a pipe handle the caller has already pinned; reports the bytes waiting without reading them.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool PeekNamedPipe(
        IntPtr hNamedPipe,
        IntPtr lpBuffer,
        uint nBufferSize,
        IntPtr lpBytesRead,
        uint* lpTotalBytesAvail,
        IntPtr lpBytesLeftThisMessage);

    /// <summary>Overlapped write: pass a null byte count and read the count from the overlapped structure.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteFile(
        SafeFileHandle hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToWrite,
        IntPtr lpNumberOfBytesWritten,
        NativeOverlapped* lpOverlapped);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, void* lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>: a waitable timer that fires at sub-millisecond resolution without
    /// raising the system timer's resolution. Windows 10 1803 and later; earlier versions fail the call with
    /// <c>ERROR_INVALID_PARAMETER</c>.
    /// </summary>
    internal const uint CreateWaitableTimerHighResolution = 0x00000002;

    /// <summary><c>TIMER_MODIFY_STATE | SYNCHRONIZE</c>: enough to set, cancel, and wait for a timer.</summary>
    internal const uint TimerModifyStateAndSynchronize = 0x0002 | 0x00100000;

    /// <summary>Creates an unnamed synchronization (auto-reset) waitable timer.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    /// <summary>
    /// Arms a waitable timer: <paramref name="lpDueTime"/> in 100 ns units, negative for a time relative to now, then every
    /// <paramref name="lPeriod"/> milliseconds, or once when it is 0.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimerEx(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        IntPtr wakeContext,
        uint tolerableDelay);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelWaitableTimer(SafeWaitHandle hTimer);

}
