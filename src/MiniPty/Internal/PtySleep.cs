using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

/// <summary>
/// Best-effort short wait helper for PTY drain and poll loops.
/// On Windows it uses a waitable timer for finer-grained waits; on other platforms
/// it falls back to <see cref="Thread.Sleep(int)"/>. The type is partial because
/// <see cref="LibraryImportAttribute"/>-based P/Invoke is source-generated into the same type.
/// </summary>
internal static partial class PtySleep
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    internal static void Sleep(int milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        if (milliseconds == 0)
        {
            Thread.Sleep(0);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Thread.Sleep(milliseconds);
            return;
        }

        using var timer = CreateWaitableTimer();
        if (timer is null || timer.IsInvalid)
        {
            Thread.Sleep(milliseconds);
            return;
        }

        var dueTime = -(milliseconds * 10_000L);
        if (!SetWaitableTimer(timer, in dueTime, 0, 0, 0, false))
        {
            Thread.Sleep(milliseconds);
            return;
        }

        if (WaitForSingleObject(timer, Infinite) != 0)
        {
            Thread.Sleep(milliseconds);
        }
    }

    private static SafeWaitHandle? CreateWaitableTimer()
    {
        try
        {
            var handle = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
            return handle.IsInvalid ? null : handle;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(
        nint lpTimerAttributes,
        nint lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long pDueTime,
        int lPeriod,
        nint pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeWaitHandle hHandle, uint dwMilliseconds);
}
