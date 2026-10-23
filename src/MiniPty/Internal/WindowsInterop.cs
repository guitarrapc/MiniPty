using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

internal static partial class WindowsInterop
{
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint WaitFailed = 0xFFFFFFFF;
    internal const uint PipeWait = 0;
    internal const uint PipeNowait = 1;
    internal const int ErrorNoData = 232;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(
        SafeFileHandle hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    /// <summary>For a handle the caller has already pinned (<see cref="SafeHandle.DangerousAddRef"/>).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr CreateEventW(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenThread(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwThreadId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    /// <summary>
    /// Cancels the synchronous I/O in progress on <paramref name="hThread"/> (which needs <c>THREAD_TERMINATE</c>
    /// access); the canceled call fails with <c>ERROR_OPERATION_ABORTED</c>. Fails with <c>ERROR_NOT_FOUND</c>
    /// when nothing is in progress.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelSynchronousIo(IntPtr hThread);

    /// <summary>For a handle the caller has already pinned (<see cref="SafeHandle.DangerousAddRef"/>).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetNamedPipeHandleState(
        IntPtr hNamedPipe,
        uint* lpMode,
        IntPtr lpMaxCollectionCount,
        IntPtr lpCollectDataTimeout);
}
