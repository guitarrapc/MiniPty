using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniPty.Internal;

/// <summary>
/// Observer of the exits of the ConPTY children this load of MiniPty starts: background threads that each wait
/// on up to 63 child process handles with <c>WaitForMultipleObjects</c>, so observing exit adds no thread per
/// session and never occupies a thread-pool worker.
/// </summary>
/// <remarks>
/// <para>
/// Each watched child is tracked through a duplicate of its process handle that the watcher owns and
/// closes when the child exits. The session can therefore close its own handle at any time without
/// unregistering first: closing a handle that another thread is waiting on is undefined behavior, and the
/// duplicate rules that out. A disposed session's child is killed, so its slot always drains.
/// </para>
/// <para>
/// Registration allocates nothing per session: slots live in fixed arrays owned by each watcher thread.
/// <c>RegisterWaitForSingleObject</c> was measured at roughly 270 bytes per session (wait handle wrappers,
/// the registration object, its lock, a callback wrapper, and a completion work item).
/// </para>
/// </remarks>
internal static partial class WindowsExitWatcher
{
    /// <summary>
    /// The wait limit is 64 handles (MAXIMUM_WAIT_OBJECTS); slot 0 is each thread's wake event.
    /// </summary>
    private const int MaxWaitObjects = 64;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint DuplicateSameAccess = 0x00000002;

    private static readonly Lock s_lock = new();
    private static readonly List<WatcherThread> s_threads = [];

    /// <summary>
    /// Starts watching <paramref name="processHandle"/>; <paramref name="onExited"/> runs on a watcher thread
    /// once the process exits. The caller keeps ownership of its own handle.
    /// </summary>
    internal static void Register(IntPtr processHandle, IWindowsExitObserver onExited)
    {
        var currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(currentProcess, processHandle, currentProcess, out var duplicate, 0, false, DuplicateSameAccess))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateHandle failed");

        lock (s_lock)
        {
            foreach (var thread in s_threads)
            {
                if (thread.TryAdd(duplicate, onExited))
                    return;
            }

            var created = new WatcherThread();
            s_threads.Add(created);
            created.TryAdd(duplicate, onExited);
        }
    }

    private sealed class WatcherThread
    {
        private readonly Lock _lock = new();
        private readonly IntPtr _wakeEvent;
        // Index 0 is the wake event; children occupy 1.._count - 1.
        private readonly IntPtr[] _handles = new IntPtr[MaxWaitObjects];
        private readonly IWindowsExitObserver?[] _observers = new IWindowsExitObserver?[MaxWaitObjects];
        private int _count = 1;

        public WatcherThread()
        {
            _wakeEvent = CreateEventW(IntPtr.Zero, bManualReset: false, bInitialState: false, IntPtr.Zero);
            if (_wakeEvent == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEvent failed");

            _handles[0] = _wakeEvent;
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MiniPty exit watcher",
            };
            thread.Start();
        }

        public bool TryAdd(IntPtr duplicate, IWindowsExitObserver observer)
        {
            lock (_lock)
            {
                if (_count == MaxWaitObjects)
                    return false;

                _handles[_count] = duplicate;
                _observers[_count] = observer;
                _count++;
            }

            // The thread snapshots its slots before each wait, so wake it to include the new child.
            SetEvent(_wakeEvent);
            return true;
        }

        private unsafe void Run()
        {
            var snapshot = new IntPtr[MaxWaitObjects];
            while (true)
            {
                int count;
                lock (_lock)
                {
                    count = _count;
                    Array.Copy(_handles, snapshot, count);
                }

                uint result;
                fixed (IntPtr* handles = snapshot)
                    result = WaitForMultipleObjects((uint)count, handles, bWaitAll: false, Infinite);

                if (result == WaitFailed)
                {
                    // Not expected: every handle in the set is a live handle this thread owns. Back off
                    // rather than spin, then retry with a fresh snapshot.
                    Thread.Sleep(10);
                    continue;
                }

                var index = (int)result;
                if (index <= 0 || index >= count)
                    continue; // The wake event, or an abandoned-mutex code that cannot apply here.

                Complete(snapshot[index]);
            }
        }

        private void Complete(IntPtr duplicate)
        {
            IWindowsExitObserver? observer = null;
            lock (_lock)
            {
                // Slots may have moved since the snapshot; find the handle and swap the last slot into it.
                for (var i = 1; i < _count; i++)
                {
                    if (_handles[i] != duplicate)
                        continue;

                    observer = _observers[i];
                    _count--;
                    _handles[i] = _handles[_count];
                    _observers[i] = _observers[_count];
                    _handles[_count] = IntPtr.Zero;
                    _observers[_count] = null;
                    break;
                }
            }

            CloseHandle(duplicate);
            observer?.OnProcessExited();
        }
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
    private static partial IntPtr CreateEventW(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint WaitForMultipleObjects(
        uint nCount,
        IntPtr* lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);
}

/// <summary>Receives a child exit from <see cref="WindowsExitWatcher"/> on a watcher thread.</summary>
internal interface IWindowsExitObserver
{
    void OnProcessExited();
}
