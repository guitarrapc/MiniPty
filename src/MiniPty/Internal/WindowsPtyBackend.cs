using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct WindowsCoord(short x, short y)
{
    public readonly short X = x;
    public readonly short Y = y;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsStartupInfo
{
    public int cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsStartupInfoEx
{
    public WindowsStartupInfo StartupInfo;
    public IntPtr lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsProcessInformation
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}

/// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsJobBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

/// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>; only the basic limit flags are set.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsJobExtendedLimitInformation
{
    public WindowsJobBasicLimitInformation BasicLimitInformation;
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

internal static class WindowsPtyBackend
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    /// <summary>
    /// <c>PSEUDOCONSOLE_INHERIT_CURSOR</c>, the one <c>CreatePseudoConsole</c> flag whose value has stayed the same
    /// across in-box and redistributable ConPTY builds. See <see cref="PtyStartInfo.InheritCursor"/>.
    /// </summary>
    private const uint PseudoConsoleInheritCursor = 0x1;
    /// <summary>
    /// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>: the child starts inside the listed jobs, so it cannot exit or start a
    /// process of its own before it is assigned. See <see cref="PtyStartInfo.KillProcessTree"/>.
    /// </summary>
    private const int ProcThreadAttributeJobList = 0x0002000D;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    // EOF staging advances one step per tick, matching the cadence of the exit-wait poll loop it replaced.
    private const int EofTickMs = 100;
    private const byte InputEofCtrlZ = 0x1A;
    private const byte InputEofSubmit = 0x0D;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = WindowsInterop.ErrorNoData;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;

    // Why the input pipe closed, which decides how queued writes fail. EOF never closes it: ConPTY observes a closed
    // input pipe as STATUS_CONTROL_C_EXIT for its clients, not as end of input (see PromoteEofIfPending).
    private const int InputOutputEnded = 1;
    private const int InputDisposed = 2;

    /// <summary>
    /// The input pipe's buffer, and the most one overlapped write carries. It keeps the anonymous pipe's default
    /// size.
    /// </summary>
    private const int InputPipeBufferSize = 4096;
    private const string InputPipeNamePrefix = @"\\.\pipe\minipty-input-";
    /// <summary>
    /// The output pipe's buffer: what ConPTY can write ahead of MiniPty's reads, beyond the ring. It bounds the output
    /// still in flight when a child is interrupted during a flood, and keeps the anonymous pipe's default size.
    /// </summary>
    private const int OutputPipeBufferSize = 4096;
    private const string OutputPipeNamePrefix = @"\\.\pipe\minipty-output-";
    private const uint PipeAccessInbound = 0x00000001;
    private const uint PipeAccessOutbound = 0x00000002;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    // PIPE_TYPE_BYTE, PIPE_READMODE_BYTE, and PIPE_WAIT are zero.
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityIdentification = 0x00010000;
    private const byte FileSkipCompletionPortOnSuccess = 0x1;
    private const byte FileSkipSetEventOnHandle = 0x2;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static IPtyBackend Start(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        // Decided before any handle exists, so an invalid environment entry, a batch file's rejected argument, or a
        // missing program or script fails without cleanup. The environment comes first because the launch depends on
        // it: a child PATH the overlay sets or removes is where a program name is looked up.
        var environment = PtyEnvironment.BuildWindows(startInfo, out var pathOverridden, out var childPath);
        var (applicationName, commandLine) = BuildLaunch(startInfo, pathOverridden, childPath);

        CreateConPtyPipes(out var inputRead, out var inputWrite, out var outputRead, out var outputWrite);
        var inputWriteHandle = inputWrite;
        var outputReadHandle = outputRead;

        var size = startInfo.ClampedSize;
        var coord = new WindowsCoord((short)size.Columns, (short)size.Rows);
        var pseudoConsoleFlags = startInfo.InheritCursor ? PseudoConsoleInheritCursor : 0;
        // The application's out-of-band ConPTY, or null for the one Windows ships (see PtyStartInfo.ConPty). Every
        // pseudo-console call of the session goes through the implementation that created it, which the handle
        // carries; a session on the in-box ConPTY keeps its handle and its allocation unchanged.
        var library = startInfo.ConPty;
        IntPtr pseudoConsoleHandle;
        int hr;
        if (library is null)
            hr = WindowsInterop.CreatePseudoConsole(coord, inputRead, outputWrite, pseudoConsoleFlags, out pseudoConsoleHandle);
        else
            hr = library.CreatePseudoConsole(coord, inputRead, outputWrite, pseudoConsoleFlags, out pseudoConsoleHandle);
        if (hr < 0)
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        SafePseudoConsoleHandle hpc = library is null
            ? new SafePseudoConsoleHandle(pseudoConsoleHandle)
            : new OutOfBandPseudoConsoleHandle(pseudoConsoleHandle, library);
        inputRead.Dispose();
        outputWrite.Dispose();

        var attrList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var processInfo = new WindowsProcessInformation();
        SafeKernelHandle? process = null;
        SafeKernelHandle? job = null;
        try
        {
            if (startInfo.KillProcessTree)
                job = CreateKillOnCloseJob();

            var attributeCount = job is null ? 1 : 2;
            nuint attrListSize = 0;
            _ = WindowsInterop.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attrListSize);
            if (attrListSize == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList size query failed");

            // An attribute's value must stay valid until the list is deleted, which the session does at disposal. The
            // job list is an array of handles rather than a handle, so its one slot sits after the list in the same
            // allocation, and freeing the list frees it too.
            var jobSlotOffset = (attrListSize + (nuint)IntPtr.Size - 1) & ~((nuint)IntPtr.Size - 1);
            var allocationSize = job is null ? attrListSize : jobSlotOffset + (nuint)IntPtr.Size;
            attrList = Marshal.AllocHGlobal((IntPtr)(nint)allocationSize);
            if (!WindowsInterop.InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref attrListSize))
            {
                var error = Marshal.GetLastPInvokeError();
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;
                throw new Win32Exception(error, "InitializeProcThreadAttributeList failed");
            }

            if (!WindowsInterop.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    hpc.DangerousGetHandle(),
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed");
            }

            if (job is not null)
            {
                var jobSlot = attrList + (nint)jobSlotOffset;
                Marshal.WriteIntPtr(jobSlot, job.DangerousGetHandle());
                if (!WindowsInterop.UpdateProcThreadAttribute(
                        attrList,
                        0,
                        (IntPtr)ProcThreadAttributeJobList,
                        jobSlot,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute (job list) failed");
                }
            }

            var startupInfo = new WindowsStartupInfoEx
            {
                StartupInfo =
                {
                    cb = Marshal.SizeOf<WindowsStartupInfoEx>(),
                    dwFlags = (int)StartfUseStdHandles,
                    hStdInput = InvalidHandleValue,
                    hStdOutput = InvalidHandleValue,
                    hStdError = InvalidHandleValue,
                },
                lpAttributeList = attrList,
            };

            var creationFlags = ExtendedStartupInfoPresent;
            if (environment is not null)
            {
                environmentBlock = AllocEnvironmentBlock(environment);
                creationFlags |= CreateUnicodeEnvironment;
            }

            if (!WindowsInterop.CreateProcessW(
                    applicationName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    environmentBlock,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed");
            }

            // Nothing uses the primary thread's handle. The process handle moves into a SafeHandle, which exit queries
            // and kills pin while they use it.
            WindowsInterop.CloseHandle(processInfo.hThread);
            processInfo.hThread = IntPtr.Zero;
            process = new SafeKernelHandle(processInfo.hProcess);
            processInfo.hProcess = IntPtr.Zero;

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
                environmentBlock = IntPtr.Zero;
            }

            // The child holds its own reference to the console server now, so MiniPty drops the one that kept the host
            // alive until then. The host exits with its last client and breaks the output pipe, which ends output without
            // the post-exit quiet interval; resize and clear keep working, as they use the signal pipe. The out-of-band
            // ConPTY always can; the in-box one can from Windows 11 24H2 (build 26100), and on older builds the session
            // ends on the drain's close as before. Never fatal: a failed release leaves the session as if not tried.
            _ = hpc.ReleaseServerReference();

            PtyStartTestHook.Run(PtyStartStage.Spawned, processInfo.dwProcessId);
            return new WindowsPtyBackendInstance(
                inputWriteHandle,
                outputReadHandle,
                hpc,
                attrList,
                process,
                job,
                processInfo.dwProcessId,
                size);
        }
        catch
        {
            if (processInfo.hThread != IntPtr.Zero)
                WindowsInterop.CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
                WindowsInterop.CloseHandle(processInfo.hProcess);

            // The child started but the session could not be built around it: end it now rather than when closing the
            // pseudo-console below detaches it.
            if (process is not null)
                WindowsInterop.TerminateProcess(process.DangerousGetHandle(), 1);

            // The order Dispose closes them in: input, pseudo-console, output, the process handle, then the job, whose
            // close ends whatever the child had started meanwhile.
            inputWriteHandle.Dispose();
            hpc.Dispose();
            outputReadHandle.Dispose();
            process?.Dispose();
            job?.Dispose();
            if (attrList != IntPtr.Zero)
            {
                WindowsInterop.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }

            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);

            throw;
        }
    }

    /// <summary>
    /// Creates the job of <see cref="PtyStartInfo.KillProcessTree"/>: kill-on-close and nothing else, so closing its only
    /// handle, at disposal or when the host process ends in any way, ends every process in it. Breakaway is not allowed,
    /// so no descendant can leave. Nested inside a job the host may already run in (Windows 8 and later).
    /// </summary>
    private static unsafe SafeKernelHandle CreateKillOnCloseJob()
    {
        var job = new SafeKernelHandle(WindowsInterop.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero));
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "CreateJobObject failed");
        }

        var limits = new WindowsJobExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!WindowsInterop.SetInformationJobObject(
                job.DangerousGetHandle(),
                JobObjectExtendedLimitInformationClass,
                &limits,
                (uint)sizeof(WindowsJobExtendedLimitInformation)))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed");
        }

        return job;
    }

    private static unsafe IntPtr AllocEnvironmentBlock(KeyValuePair<string, string>[] environment)
    {
        Array.Sort(environment, static (left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));

        var charCount = 1;
        for (var i = 0; i < environment.Length; i++)
        {
            if (environment[i].Value.Length == 0)
                continue;

            charCount += environment[i].Key.Length + 1 + environment[i].Value.Length + 1;
        }

        if (charCount == 1)
            charCount = 2;

        var block = Marshal.AllocHGlobal(charCount * sizeof(char));
        var span = new Span<char>((void*)block, charCount);
        var offset = 0;
        for (var i = 0; i < environment.Length; i++)
        {
            var pair = environment[i];
            if (pair.Value.Length == 0)
                continue;

            pair.Key.AsSpan().CopyTo(span[offset..]);
            offset += pair.Key.Length;
            span[offset++] = '=';
            pair.Value.AsSpan().CopyTo(span[offset..]);
            offset += pair.Value.Length;
            span[offset++] = '\0';
        }

        span[offset] = '\0';
        if (offset == 0)
            span[1] = '\0';
        return block;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed unsafe class WindowsPtyBackendInstance : IPtyBackend, IPtyInputQueue, IWindowsExitObserver, IValueTaskSource<int>
    {
        private static readonly IOCompletionCallback s_inputWriteCompleted = OnInputWriteCompleted;

        // MiniPty's end of the input pipe, opened overlapped (see CreateConPtyPipes).
        private readonly SafeFileHandle _inputWriteHandle;
        // MiniPty's end of the output pipe, opened overlapped and read by WindowsOutputDriver.
        private readonly SafeFileHandle _outputReadHandle;
        // Resizes, clears, and closes through the ConPTY implementation that created the pseudo-console.
        private readonly SafePseudoConsoleHandle _hpc;
        private readonly IntPtr _attrList;
        // Pinned for every native call (see TryPin), because Dispose can release it while the exit watcher, the
        // EOF timer, or a caller is still using it.
        private readonly SafeKernelHandle _process;
        // The kill-on-close job of PtyStartInfo.KillProcessTree, or null. Pinned like the process handle.
        private readonly SafeKernelHandle? _job;
        private readonly int _processId;
        // Mutable struct: used only through this field, never copied.
        private PtyExitSignal _exitSignal;
        private Timer? _eofTimer;
        private PtySize _size;
        private bool _inputClosed;
        private bool _outputClosed;
        /// <summary>
        /// EOF took effect: the Ctrl+Z / CR chord was written, or the input pipe had closed before it could be. The pipe
        /// is never closed for EOF; it stays open until the child exits.
        /// </summary>
        private bool _eofSignaled;
        /// <summary>EOF was requested and the chord is still to be written, by the staging timer or the transport close.</summary>
        private bool _eofPending;
        private bool _hpcClosed;
        private bool _exited;
        private int _exitCode;
        private bool _disposed;

        // Input queue and overlapped write state (see InputState), created on the first write. Guarded by lock (this),
        // which also orders EOF bytes and the pipe's close with writes; locking the instance itself, which is never
        // exposed, adds no object to the session.
        private InputState? _input;

        private bool IsInputIdleLocked => _input is not { } input || input.IsIdle;

        public WindowsPtyBackendInstance(
            SafeFileHandle inputWriteHandle,
            SafeFileHandle outputReadHandle,
            SafePseudoConsoleHandle hpc,
            IntPtr attrList,
            SafeKernelHandle process,
            SafeKernelHandle? job,
            int processId,
            PtySize size)
        {
            _inputWriteHandle = inputWriteHandle;
            _outputReadHandle = outputReadHandle;
            _hpc = hpc;
            _attrList = attrList;
            _process = process;
            _job = job;
            _processId = processId;
            _size = size;
            Input = new PtyInputStream(this);

            // Last, so the watcher never observes a partially constructed instance. Nothing can release the handle yet.
            WindowsExitWatcher.Register(process.DangerousGetHandle(), this);
        }

        public Stream Input { get; }

        public PtyPendingInput? QueueInput(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return null;

            lock (this)
            {
                ThrowIfInputClosedLocked();
                var input = EnsureInputLocked();
                var issued = 0;
                if (input.IsIdle)
                {
                    issued = WriteNowLocked(input, bytes);
                    if (issued == bytes.Length && !input.WriteInFlight)
                        return null;
                }

                // The caller's span does not outlive this call, so what is left is copied.
                var rest = bytes[issued..];
                byte[]? owned = null;
                if (!rest.IsEmpty)
                {
                    owned = ArrayPool<byte>.Shared.Rent(rest.Length);
                    rest.CopyTo(owned);
                }

                return EnqueueLocked(input, owned is null ? default : owned.AsMemory(0, rest.Length), owned, ownsStaged: issued > 0);
            }
        }

        public PtyPendingInput? QueueInput(ReadOnlyMemory<byte> bytes, byte[]? ownedBuffer)
        {
            // Returned to the pool here unless a queued entry takes it over.
            var owned = ownedBuffer;
            try
            {
                if (bytes.IsEmpty)
                    return null;

                lock (this)
                {
                    ThrowIfInputClosedLocked();
                    var input = EnsureInputLocked();
                    var issued = 0;
                    if (input.IsIdle)
                    {
                        issued = WriteNowLocked(input, bytes.Span);
                        if (issued == bytes.Length && !input.WriteInFlight)
                            return null;
                    }

                    // Bytes already staged no longer need the caller's buffer, so it is kept only for the rest.
                    var rest = bytes[issued..];
                    var keep = rest.IsEmpty ? null : owned;
                    var entry = EnqueueLocked(input, rest, keep, ownsStaged: issued > 0);
                    if (keep is not null)
                        owned = null;

                    return entry;
                }
            }
            finally
            {
                if (owned is not null)
                    ArrayPool<byte>.Shared.Return(owned);
            }
        }

        public void CancelInput(PtyPendingInput entry)
        {
            var removed = false;
            lock (this)
            {
                if (_input is { } input && PtyPendingInput.Remove(ref input.Head, ref input.Tail, entry))
                {
                    // Its bytes in flight still complete; the queue then moves on without it.
                    if (input.StagedOwner == entry)
                        input.StagedOwner = null;

                    removed = true;
                }
            }

            if (removed)
                entry.Complete(new OperationCanceledException(entry.Token));
        }

        // Input is written by its own I/O completions on Windows, so input never needs to wake the output driver.
        public void SetOutputPump(PtyOutputPump pump)
        {
        }

        /// <summary>Throws once the input pipe is closed. Caller holds lock (this).</summary>
        private void ThrowIfInputClosedLocked()
        {
            if (!_inputClosed)
                return;

            if (_disposed)
                throw new ObjectDisposedException(nameof(PtySession));

            throw new IOException("PTY input cannot be written: the input pipe is closed.");
        }

        /// <summary>
        /// Returns the session's input state, creating it on the first write: binds the input pipe to the runtime's
        /// I/O completion port and creates the reusable overlapped structure and staging buffer. Caller holds lock
        /// (this) and has checked that the pipe is open.
        /// </summary>
        private InputState EnsureInputLocked()
        {
            var input = _input ??= new InputState();
            if (input.Overlapped is not null)
                return input;

            input.Binding ??= ThreadPoolBoundHandle.BindHandle(_inputWriteHandle);
            // A write that completes at once is finished by its caller; a queued completion would finish it twice.
            if (!WindowsInterop.SetFileCompletionNotificationModes(
                    _inputWriteHandle,
                    FileSkipCompletionPortOnSuccess | FileSkipSetEventOnHandle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetFileCompletionNotificationModes failed");
            }

            if (input.Staging is null)
                input.Staging = (byte*)NativeMemory.Alloc(InputPipeBufferSize);

            input.Overlapped = PreAllocatedOverlapped.UnsafeCreate(s_inputWriteCompleted, this, null);
            return input;
        }

        /// <summary>
        /// Appends a queued write. <paramref name="ownsStaged"/> marks the bytes this call already staged as its own,
        /// so it completes only once they are written too; the queue was idle then, so the entry is the head. Caller
        /// holds lock (this).
        /// </summary>
        private PtyPendingInput EnqueueLocked(InputState input, ReadOnlyMemory<byte> remaining, byte[]? owned, bool ownsStaged)
        {
            var entry = PtyPendingInput.Rent(this, remaining, owned);
            PtyPendingInput.Enqueue(ref input.Head, ref input.Tail, entry);
            if (ownsStaged)
                input.StagedOwner = entry;

            return entry;
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> through the staging buffer, one pipe-buffer-sized part at a time, until a
        /// write is left pending. Returns the byte count staged; the last part staged may still be in flight. Caller
        /// holds lock (this) and has checked that the input is idle.
        /// </summary>
        private int WriteNowLocked(InputState input, ReadOnlySpan<byte> bytes)
        {
            var issued = 0;
            while (issued < bytes.Length)
            {
                var count = Math.Min(bytes.Length - issued, InputPipeBufferSize);
                StageInputLocked(input, bytes.Slice(issued, count), owner: null);
                issued += count;
                if (!WriteStagedLocked(input))
                    break;
            }

            return issued;
        }

        /// <summary>
        /// Stages caller bytes and records their last byte for EOF. Staged bytes always reach the pipe, while a canceled
        /// write discards what was never staged, so EOF must go by the last byte staged. Caller holds lock (this).
        /// </summary>
        private void StageInputLocked(InputState input, ReadOnlySpan<byte> bytes, PtyPendingInput? owner)
        {
            StageLocked(input, bytes, owner);
            if (!bytes.IsEmpty)
                input.LastStagedByte = bytes[^1];
        }

        /// <summary>Copies bytes into the empty staging buffer. Caller holds lock (this).</summary>
        private static void StageLocked(InputState input, ReadOnlySpan<byte> bytes, PtyPendingInput? owner)
        {
            bytes.CopyTo(new Span<byte>(input.Staging, InputPipeBufferSize));
            input.StagedOffset = 0;
            input.StagedLength = bytes.Length;
            input.StagedOwner = owner;
        }

        /// <summary>
        /// Writes the staged bytes. Returns <see langword="true"/> once the pipe has taken them all, and
        /// <see langword="false"/> when a write is left pending; its completion continues the queue. Caller holds
        /// lock (this).
        /// </summary>
        private bool WriteStagedLocked(InputState input)
        {
            while (input.StagedLength > 0)
            {
                var overlapped = input.Binding!.AllocateNativeOverlapped(input.Overlapped!);
                if (WindowsInterop.WriteFile(
                        _inputWriteHandle,
                        input.Staging + input.StagedOffset,
                        (uint)input.StagedLength,
                        IntPtr.Zero,
                        overlapped))
                {
                    // Completed at once. Completion-port notification is skipped on success, so no callback follows.
                    var written = (int)overlapped->InternalHigh;
                    input.Binding.FreeNativeOverlapped(overlapped);
                    AdvanceStagedLocked(input, written);
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorIoPending)
                {
                    input.WriteInFlight = true;
                    return false;
                }

                input.Binding.FreeNativeOverlapped(overlapped);
                input.StagedLength = 0;
                throw InputWriteFailed(error);
            }

            return true;
        }

        private static void AdvanceStagedLocked(InputState input, int written)
        {
            if (written <= 0)
            {
                input.StagedLength = 0;
                throw new IOException("WriteFile wrote 0 bytes");
            }

            written = Math.Min(written, input.StagedLength);
            input.StagedOffset += written;
            input.StagedLength -= written;
        }

        /// <summary>
        /// Writes staged and queued input until a write is left pending or nothing is queued, collecting the entries
        /// that finished into a chain for the caller to complete outside the lock. Caller holds lock (this).
        /// </summary>
        private void FlushInputLocked(InputState input, ref PtyPendingInput? done)
        {
            PtyPendingInput? doneTail = null;
            while (WriteStagedLocked(input))
            {
                var owner = input.StagedOwner;
                input.StagedOwner = null;
                if (owner is not null && owner.Remaining.IsEmpty)
                {
                    // The owner of staged bytes is the head, so it leaves the queue from the front.
                    PtyPendingInput.DequeueHead(ref input.Head, ref input.Tail);
                    if (doneTail is null)
                        done = owner;
                    else
                        doneTail.Next = owner;

                    doneTail = owner;
                }

                var head = input.Head;
                if (head is null)
                    return;

                var count = Math.Min(head.Remaining.Length, InputPipeBufferSize);
                StageInputLocked(input, head.Remaining.Span[..count], head);
                head.Remaining = head.Remaining[count..];
            }
        }

        private static void OnInputWriteCompleted(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
        {
            var instance = (WindowsPtyBackendInstance)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
            instance.OnInputWriteCompleted(errorCode, (int)numBytes, overlapped);
        }

        /// <summary>
        /// Completion of a pending write, on a thread-pool I/O thread: writes on through the queue and completes the
        /// writes that finished. Must never throw.
        /// </summary>
        private void OnInputWriteCompleted(uint errorCode, int written, NativeOverlapped* overlapped)
        {
            PtyPendingInput? done = null;
            PtyPendingInput? failed = null;
            Exception? error = null;
            lock (this)
            {
                var input = _input!;
                input.Binding!.FreeNativeOverlapped(overlapped);
                input.WriteInFlight = false;
                if (_inputClosed)
                {
                    // The pipe was closed under the write, and closing failed what was queued.
                    ReleaseInputResourcesLocked();
                    return;
                }

                try
                {
                    if (errorCode != 0)
                    {
                        input.StagedLength = 0;
                        throw InputWriteFailed((int)errorCode);
                    }

                    AdvanceStagedLocked(input, written);
                    FlushInputLocked(input, ref done);
                }
                catch (Exception ex)
                {
                    // A pipe write that fails does not recover, so the queued writes fail with it instead of waiting.
                    error = ex;
                    failed = PtyPendingInput.DetachAll(ref input.Head, ref input.Tail);
                    input.StagedOwner = null;
                }
            }

            // Completed outside the lock: a waiter's continuation must never run while the queue is held.
            PtyPendingInput.CompleteChain(done, null);
            PtyPendingInput.CompleteChain(failed, error);
        }

        /// <summary>
        /// Closes MiniPty's end of the input pipe and fails what is queued: with <see cref="ObjectDisposedException"/>
        /// on disposal, otherwise with <see cref="IOException"/>. A write in flight is canceled, and its completion
        /// releases the overlapped resources.
        /// </summary>
        private void CloseInputPipe(int reason)
        {
            PtyPendingInput? queued = null;
            lock (this)
            {
                if (_inputClosed)
                    return;

                _inputClosed = true;
                if (_input is { } input)
                {
                    queued = PtyPendingInput.DetachAll(ref input.Head, ref input.Tail);
                    input.StagedOwner = null;
                    if (input.WriteInFlight)
                        _ = WindowsInterop.CancelIoEx(_inputWriteHandle, IntPtr.Zero);
                }

                _inputWriteHandle.Dispose();
                ReleaseInputResourcesLocked();
            }

            if (queued is null)
                return;

            Exception error = reason == InputDisposed
                ? new ObjectDisposedException(nameof(PtySession))
                : new IOException("PTY input cannot be written: the input pipe is closed.");
            PtyPendingInput.CompleteChain(queued, error);
        }

        /// <summary>
        /// Releases the overlapped structure, the completion-port binding, and the staging buffer once the pipe is
        /// closed and no write is in flight; a write in flight releases them from its completion. Caller holds lock
        /// (this).
        /// </summary>
        private void ReleaseInputResourcesLocked()
        {
            if (_input is not { } input || input.WriteInFlight)
                return;

            input.Overlapped?.Dispose();
            input.Overlapped = null;
            input.Binding?.Dispose();
            input.Binding = null;
            if (input.Staging is not null)
            {
                NativeMemory.Free(input.Staging);
                input.Staging = null;
            }
        }

        private static IOException InputWriteFailed(int error) =>
            error is ErrorBrokenPipe or ErrorNoData or ErrorOperationAborted
                ? new IOException("PTY input cannot be written: the pseudo-console has closed.")
                : new IOException($"WriteFile failed (Win32 {error})");

        /// <summary>
        /// Input state of a session that has written input. MiniPty's end of the input pipe is
        /// overlapped: a write the pipe takes at once completes before the call returns, and the rest is queued in
        /// issue order and written by I/O completions, so a write never blocks its caller while ConPTY is not reading
        /// input. Created on the first write, so a session that never writes carries only one reference.
        /// Guarded by the session's lock.
        /// </summary>
        private sealed class InputState
        {
            internal PtyPendingInput? Head;
            internal PtyPendingInput? Tail;
            internal ThreadPoolBoundHandle? Binding;
            internal PreAllocatedOverlapped? Overlapped;

            /// <summary>
            /// Native buffer every write is issued from: an overlapped write needs its bytes until it completes, which
            /// can be after the caller's buffer is gone. Freed only when no write is in flight.
            /// </summary>
            internal byte* Staging;
            internal int StagedOffset;
            internal int StagedLength;

            /// <summary>
            /// Entry whose bytes are staged; it completes once they and the rest of it are written. Null for EOF
            /// bytes, and for an entry canceled while its bytes were in flight. When set, it is the queue head.
            /// </summary>
            internal PtyPendingInput? StagedOwner;
            internal bool WriteInFlight;

            /// <summary>
            /// Last caller byte staged, or -1 before any. EOF goes by it to decide whether a line must be submitted
            /// first. An int, because every byte value is valid input and none can mark that nothing was written. It
            /// lives here rather than on the session, so a session that never writes does not pay for it.
            /// </summary>
            internal int LastStagedByte = -1;

            internal bool IsIdle => Head is null && StagedLength == 0 && !WriteInFlight;
        }

        public int ProcessId => _processId;

        public Task<PtyExitStatus> Exited => _exitSignal.GetTask();

        public ValueTask<int> WaitForExitCodeAsync() => _exitSignal.WaitForExitCodeAsync(this);

        public void SetExitListener(IPtyExitListener listener) => _exitSignal.SetListener(listener);

        int IValueTaskSource<int>.GetResult(short token) => _exitSignal.GetWaiterResult(token);

        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _exitSignal.GetWaiterStatus(token);

        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _exitSignal.OnWaiterCompleted(continuation, state, token, flags);

        public bool HasExited
        {
            get
            {
                TryRefreshExitState();
                return _exited;
            }
        }

        public int ExitCode
        {
            get
            {
                TryRefreshExitState();
                return _exitCode;
            }
        }

        // Windows has no signal concept for exit reporting; ConPTY children always report null.
        public int? ExitSignal => null;

        public PtySize Size => _size;

        public string? ActiveProcessName => null;

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Frontend size events race child exit. Once post-exit drain has closed the pseudo-console
            // there is nothing left to resize, so the call is a no-op rather than an error.
            if (_hpc.IsClosed)
                return;

            columns = PtySizeLimits.Clamp(columns);
            rows = PtySizeLimits.Clamp(rows);

            // The reference keeps the HPCON valid if drain closes the pseudo-console during the call;
            // the deferred ClosePseudoConsole then runs on release instead of under ResizePseudoConsole.
            var addedRef = false;
            try
            {
                _hpc.DangerousAddRef(ref addedRef);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            int hr;
            try
            {
                hr = _hpc.Resize(new WindowsCoord((short)columns, (short)rows));
            }
            finally
            {
                if (addedRef)
                    _hpc.DangerousRelease();
            }

            if (hr < 0)
            {
                if (_hpc.IsClosed || TryRefreshExitState())
                    return;

                throw new Win32Exception(hr, "ResizePseudoConsole failed");
            }

            _size = new PtySize(columns, rows);
        }

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The in-box ConPTY exposes no clear operation. After the post-exit drain has closed the pseudo-console
            // there is nothing left to clear, so the call is a no-op, as Resize is.
            if (!_hpc.CanClear || _hpc.IsClosed)
                return;

            var addedRef = false;
            try
            {
                _hpc.DangerousAddRef(ref addedRef);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            int hr;
            try
            {
                hr = _hpc.Clear();
            }
            finally
            {
                if (addedRef)
                    _hpc.DangerousRelease();
            }

            if (hr < 0)
            {
                if (_hpc.IsClosed || TryRefreshExitState())
                    return;

                throw new Win32Exception(hr, "ClearPseudoConsole failed");
            }
        }

        public void SendEof()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState() || _inputClosed || _eofPending || _eofSignaled)
                return;

            // Stage EOF: WriteFile can succeed before ConPTY stdin is attached. The staging timer advances
            // it, so EOF no longer depends on somebody awaiting exit.
            _eofPending = true;
            _eofTimer ??= new Timer(static state => ((WindowsPtyBackendInstance)state!).OnEofTick(), this, Timeout.Infinite, Timeout.Infinite);
            _eofTimer.Change(EofTickMs, Timeout.Infinite);
        }

        public void Kill()
        {
            if (_disposed)
                return;

            // With a job, the tree ends as it does at disposal, also after the child itself has exited: its descendants
            // may still run.
            if (_job is not null)
            {
                KillCore();
                return;
            }

            if (TryRefreshExitState())
                return;

            KillCore();
        }

        public void Kill(PtySignal signal)
        {
            // node-pty semantics: the signal is advisory on Windows and the child is terminated.
            // Validate the enum the same way the Unix backend does so misuse throws on both platforms.
            _ = signal switch
            {
                PtySignal.Hangup or PtySignal.Interrupt or PtySignal.Quit or PtySignal.Kill
                    or PtySignal.User1 or PtySignal.User2 or PtySignal.Terminate => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(signal)),
            };
            Kill();
        }

        /// <summary>
        /// Terminates the child, or with a job every process in it, unless Dispose has released the handle, in which
        /// case Dispose has already ended them. Either way the child's exit code is 1.
        /// </summary>
        private void KillCore()
        {
            var target = _job ?? _process;
            if (!TryPin(target))
                return;

            try
            {
                if (_job is not null)
                    WindowsInterop.TerminateJobObject(target.DangerousGetHandle(), 1);
                else
                    WindowsInterop.TerminateProcess(target.DangerousGetHandle(), 1);
            }
            finally
            {
                target.DangerousRelease();
            }
        }

        /// <summary>
        /// Pins the process or job handle for a native call, so a Dispose on another thread defers the close until the
        /// matching <see cref="SafeHandle.DangerousRelease"/>. Returns <see langword="false"/> once Dispose has
        /// released it. A raw handle would let the call reach whatever object reused the released value.
        /// </summary>
        private static bool TryPin(SafeHandle handle)
        {
            // Checked first, so polling exit after disposal does not throw.
            if (handle.IsClosed)
                return false;

            var pinned = false;
            try
            {
                handle.DangerousAddRef(ref pinned);
            }
            catch (ObjectDisposedException)
            {
                // Released by a Dispose on another thread since the check.
            }

            return pinned;
        }

        public void OnProcessExited()
        {
            if (!_disposed)
                TryRefreshExitState();
        }

        /// <summary>
        /// One EOF staging step, the work the exit-wait poll loop used to do on each 100 ms timeout: promote a staged
        /// EOF once input is idle.
        /// </summary>
        private void OnEofTick()
        {
            if (_disposed || _inputClosed || TryRefreshExitState())
                return;

            // A timer callback must never throw: an unhandled exception there terminates the host process.
            try
            {
                PromoteEofIfPending();
                if (!_disposed && !_inputClosed && _eofPending)
                    _eofTimer?.Change(EofTickMs, Timeout.Infinite);
            }
            catch (Exception)
            {
                // The input pipe failed, or Dispose closed it or the timer under the tick; nothing is left to signal.
            }
        }

        /// <summary>
        /// Closes the pseudo-console. ConPTY then flushes what it has rendered and closes its end of the output
        /// pipe, so the output driver drains the rest and sees end of output. The output pipe itself stays open for that
        /// drain and closes with <see cref="Dispose"/>.
        /// </summary>
        public void CloseOutputTransport()
        {
            if (!_hpcClosed)
                CloseTransport(InputOutputEnded);
        }

        public bool IsOutputClosed => Volatile.Read(ref _outputClosed);

        public int OutputChunkLimit => PtyOutputPump.Capacity;

        public int PostExitQuietMs => PtyOutputPump.DefaultPostExitQuietMs;

        /// <summary>Reads output on the completion-port loop that every session shares, holding no thread of its own.</summary>
        public void StartOutput(PtyOutputPump pump) => WindowsOutputDriver.Run(pump, _outputReadHandle);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _eofTimer?.Dispose();
            if (!TryRefreshExitState())
            {
                // The watcher owns a duplicate of the process handle, so the handle below can close at once;
                // the killed child drains the watcher's slot, and OnProcessExited ignores a disposed session.
                KillCore();
                _exitSignal.SetDisposedBeforeExit();
            }

            // Queued writes fail now, on the disposing thread, without waiting for a write in flight.
            CloseTransport(InputDisposed);
            CloseOutputPipe();

            // Closes at once, or when a concurrent exit query or kill unpins it.
            _process.Dispose();
            // Kill-on-close: ends every process still in the job, such as a descendant of a child that already exited.
            _job?.Dispose();
            if (_attrList != IntPtr.Zero)
            {
                WindowsInterop.DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
            }

            if (!_outputClosed)
            {
                _outputReadHandle.Dispose();
                _outputClosed = true;
            }
        }

        /// <summary>
        /// Records the exit if the child has exited. The exit watcher, the EOF timer, and callers all get here, also
        /// while or after Dispose releases the process handle; once it is released, a child that Dispose saw running
        /// never reports its exit.
        /// </summary>
        private bool TryRefreshExitState()
        {
            if (_exited)
                return true;

            if (!TryPin(_process))
                return _exited;

            try
            {
                var process = _process.DangerousGetHandle();
                var waitResult = WindowsInterop.WaitForSingleObject(process, 0);
                if (waitResult == WindowsInterop.WaitTimeout)
                    return false;
                if (waitResult == WindowsInterop.WaitFailed)
                    return false;

                if (!WindowsInterop.GetExitCodeProcess(process, out var exitCode))
                    return false;

                _exitCode = unchecked((int)exitCode);
                _exited = true;
            }
            finally
            {
                _process.DangerousRelease();
            }

            // Whichever path observes exit first (the watcher, HasExited, Kill, a tick) records it.
            _exitSignal.TrySetExited(new PtyExitStatus(_exitCode, null));
            return true;
        }

        private void PromoteEofIfPending()
        {
            lock (this)
            {
                if (!_eofPending)
                    return;

                // EOF goes after every byte written before it: while input is queued or in flight, a later tick
                // sends it.
                if (!IsInputIdleLocked && !_inputClosed)
                    return;

                // ConPTY observes a closed input pipe as STATUS_CONTROL_C_EXIT, not as EOF: the console host ends its
                // clients, and a child about to exit on its own loses that race under load (exit code 0xC000013A on a
                // hosted runner, and under CPU oversubscription on a desktop). That held for input that never carried
                // bytes too, which used to close the pipe after a 4 s wait. Legacy console EOF is Ctrl+Z submitted with
                // CR; the host queues it for the child whether or not the child has attached yet, so it is written on
                // the first staging tick in every case, and the pipe stays open until the child exits.
                _eofPending = false;
                WriteStreamEofToInputLocked();
                _eofSignaled = true;
            }
        }

        /// <summary>
        /// Writes the EOF chord through the staging buffer; a write left pending completes on its own. Caller holds
        /// lock (this) and has checked that nothing is queued.
        /// </summary>
        private void WriteStreamEofToInputLocked()
        {
            if (_inputClosed || _eofSignaled)
                return;

            // Ctrl+Z alone does not end input on ConPTY. A CR submits the EOF key chord. Ctrl+Z ends input only at the
            // start of a line, so a pending line is submitted first; with no byte written there is no line to submit,
            // and a leading CR would hand the child an empty line.
            var input = EnsureInputLocked();
            Span<byte> chord = stackalloc byte[3];
            var length = 0;
            if (input.LastStagedByte >= 0 && input.LastStagedByte is not '\n' and not '\r')
                chord[length++] = InputEofSubmit;

            chord[length++] = InputEofCtrlZ;
            chord[length++] = InputEofSubmit;
            StageLocked(input, chord[..length], owner: null);
            WriteStagedLocked(input);
        }

        private void CloseOutputPipe()
        {
            if (_outputClosed)
                return;

            try
            {
                _outputReadHandle.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _outputClosed = true;
        }

        private void CloseTransport(int inputReason)
        {
            try
            {
                PromoteEofIfPending();
            }
            catch (Exception)
            {
                // A failed EOF write must not skip the close below, which disposal and the post-exit drain rely on.
            }

            CloseInputPipe(inputReason);

            if (!_hpcClosed)
            {
                _hpc.Dispose();
                _hpcClosed = true;
            }
        }
    }

    /// <summary>
    /// Creates the ConPTY pipes. MiniPty's end of each is the server end of a named pipe opened overlapped (see
    /// <see cref="CreateOverlappedPipe"/>): input waits without blocking a thread while ConPTY is not taking it, and
    /// output is read through I/O completions on MiniPty's loop.
    /// </summary>
    /// <remarks>
    /// Every handle is non-inheritable. ConPTY duplicates its ends into its host process itself, and the child is
    /// created without handle inheritance.
    /// </remarks>
    private static void CreateConPtyPipes(
        out SafeFileHandle inputRead,
        out SafeFileHandle inputWrite,
        out SafeFileHandle outputRead,
        out SafeFileHandle outputWrite)
    {
        CreateOverlappedPipe(InputPipeNamePrefix, inbound: false, InputPipeBufferSize, out inputRead, out inputWrite);
        try
        {
            CreateOverlappedPipe(OutputPipeNamePrefix, inbound: true, OutputPipeBufferSize, out outputWrite, out outputRead);
        }
        catch
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates one ConPTY pipe. MiniPty's end is the server end of a named pipe opened overlapped, because an
    /// anonymous pipe cannot be: it writes input (<paramref name="inbound"/> false) or reads output. ConPTY's end is
    /// opened synchronously by name, as ConPTY uses it with blocking calls.
    /// </summary>
    /// <remarks>
    /// The name is random; the pipe allows one instance, must be the first instance of that name, and rejects remote
    /// clients. If anything else connects first, opening ConPTY's end fails and so does the spawn, instead of sharing
    /// the pipe. The buffer keeps the anonymous pipe's default size: with none, every write would wait for the other end
    /// to read it.
    /// </remarks>
    internal static unsafe void CreateOverlappedPipe(
        string namePrefix,
        bool inbound,
        int bufferSize,
        out SafeFileHandle conPtySide,
        out SafeFileHandle ourSide)
    {
        // Prefix, 32 hexadecimal digits, and the terminating NUL.
        Span<char> name = stackalloc char[namePrefix.Length + 33];
        namePrefix.CopyTo(name);
        Guid.NewGuid().TryFormat(name[namePrefix.Length..], out var digits, "N");
        name[namePrefix.Length + digits] = '\0';

        fixed (char* path = name)
        {
            ourSide = WindowsInterop.CreateNamedPipe(
                path,
                (inbound ? PipeAccessInbound : PipeAccessOutbound) | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients,
                1,
                inbound ? 0u : (uint)bufferSize,
                inbound ? (uint)bufferSize : 0u,
                0,
                IntPtr.Zero);
            if (ourSide.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                ourSide.Dispose();
                throw new Win32Exception(error, "CreateNamedPipe failed");
            }

            conPtySide = WindowsInterop.CreateFile(
                path,
                inbound ? GenericWrite : GenericRead,
                0,
                IntPtr.Zero,
                OpenExisting,
                SecuritySqosPresent | SecurityIdentification,
                IntPtr.Zero);
            if (conPtySide.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                conPtySide.Dispose();
                ourSide.Dispose();
                throw new Win32Exception(error, inbound
                    ? "Opening the ConPTY end of the output pipe failed"
                    : "Opening the ConPTY end of the input pipe failed");
            }
        }
    }

    /// <summary>
    /// The <c>CreateProcessW</c> application name and command line for <paramref name="startInfo"/>, with the child's
    /// <c>PATH</c> derived from its environment overlay (see the other overload).
    /// </summary>
    internal static (string? ApplicationName, char[] CommandLine) BuildLaunch(PtyStartInfo startInfo)
    {
        _ = PtyEnvironment.BuildWindows(startInfo, out var pathOverridden, out var childPath);
        return BuildLaunch(startInfo, pathOverridden, childPath);
    }

    /// <summary>
    /// The <c>CreateProcessW</c> application name and command line for <paramref name="startInfo"/>. A batch file
    /// without a raw <see cref="PtyStartInfo.CommandLine"/> runs through <c>cmd.exe</c> with its arguments escaped for
    /// it (<see cref="WindowsBatchFile"/>). A program image named like a batch file runs as a program from the file
    /// the same lookup found, with the file name quoted by the MSVCRT rules and the quoted arguments after it. A
    /// program name that <c>CreateProcess</c> would search for is looked up here when the overlay sets or removes
    /// <c>PATH</c> (<paramref name="pathOverridden"/>), since <c>CreateProcess</c> searches only the host's
    /// <c>PATH</c>; the file found becomes the application name, so it makes no lookup of its own. A rooted or
    /// dot-relative program path of <c>MAX_PATH</c> or more becomes the application name too, since
    /// <c>CreateProcess</c>'s own lookup fails there (<see cref="WindowsProgramLookup.TryResolveLongProgramPath"/>).
    /// Anything else is left to <c>CreateProcessW</c>'s own lookup, with the quoted arguments or the raw command line
    /// after the quoted file name.
    /// </summary>
    /// <param name="startInfo">The spawn options.</param>
    /// <param name="pathOverridden">Whether the overlay sets or removes <c>PATH</c>.</param>
    /// <param name="childPath">The child's <c>PATH</c>, or null when it has none; read only when overridden.</param>
    internal static (string? ApplicationName, char[] CommandLine) BuildLaunch(PtyStartInfo startInfo, bool pathOverridden, string? childPath)
    {
        var fileName = startInfo.FileName;
        var isBatchFile = WindowsBatchFile.IsBatchFile(fileName);
        // Only a name CreateProcess would search for is looked up on the child's PATH; a rooted or dot-relative one
        // reads neither PATH, and skipping the host locations keeps those reads off that spawn.
        var searchChildPath = pathOverridden && WindowsProgramLookup.IsSearched(fileName);
        if (startInfo.CommandLine is not { Length: > 0 } && isBatchFile)
        {
            WindowsBatchFile.ValidateName(fileName);
            var script = searchChildPath
                ? WindowsProgramLookup.ResolveScript(fileName, WindowsProgramLookup.ChildLocations(fileName, childPath))
                : WindowsProgramLookup.ResolveScriptOnHost(fileName);
            if (!WindowsBatchFile.IsExecutableImage(script))
                return (WindowsBatchFile.CommandInterpreterPath, WindowsBatchFile.BuildCommandLine(script, startInfo.Arguments));

            // A program named like a batch file runs as itself, from the file whose header was read. Left to its own
            // lookup, CreateProcess could find another file of the name, or the file could have become a script in
            // the meantime, and either would reach cmd.exe with the arguments quoted for a program instead.
            return (script, BuildCommandLine(startInfo));
        }

        // A batch file with a raw command line stays with CreateProcess, which starts cmd.exe for it as documented;
        // given as an application name, CreateProcess rebuilds the command line for cmd.exe and breaks its quoting
        // (measured: a quoted argv[0] made cmd.exe fail, and "x y" ran x).
        if (searchChildPath && !isBatchFile)
        {
            var program = WindowsProgramLookup.ResolveProgram(fileName, WindowsProgramLookup.ChildLocations(fileName, childPath));
            return (program, BuildCommandLine(startInfo));
        }

        // A rooted or dot-relative program path of MAX_PATH or more fails CreateProcess's own lookup even in a host that
        // opted into long paths; as the application name it starts there. Shorter paths stay with the lookup.
        if (!isBatchFile && WindowsProgramLookup.TryResolveLongProgramPath(fileName, out var longProgram))
            return (longProgram, BuildCommandLine(startInfo));

        return (null, BuildCommandLine(startInfo));
    }

    /// <summary>
    /// Builds the writable, NUL-terminated command line <c>CreateProcessW</c> takes: the quoted file name,
    /// then either the raw <see cref="PtyStartInfo.CommandLine"/> or each quoted argument.
    /// </summary>
    /// <remarks>
    /// Measures first and writes once into the exact-size buffer. Building through a string builder, a
    /// string, a NUL concatenation, and a char array copied the command line three times per spawn.
    /// </remarks>
    internal static char[] BuildCommandLine(PtyStartInfo startInfo)
    {
        var rawCommandLine = startInfo.CommandLine is { Length: > 0 } raw ? raw : null;
        var arguments = startInfo.Arguments;

        var length = QuotedLength(startInfo.FileName);
        if (rawCommandLine is not null)
        {
            length += 1 + rawCommandLine.Length;
        }
        else
        {
            for (var i = 0; i < arguments.Count; i++)
                length += 1 + QuotedLength(arguments[i]);
        }

        var commandLine = new char[length + 1];
        var span = commandLine.AsSpan();
        var position = WriteQuoted(span, startInfo.FileName);
        if (rawCommandLine is not null)
        {
            span[position++] = ' ';
            rawCommandLine.AsSpan().CopyTo(span[position..]);
            position += rawCommandLine.Length;
        }
        else
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                span[position++] = ' ';
                position += WriteQuoted(span[position..], arguments[i]);
            }
        }

        span[position] = '\0';
        return commandLine;
    }

    /// <summary>Length of <paramref name="arg"/> after <see cref="WriteQuoted"/>.</summary>
    private static int QuotedLength(string arg)
    {
        if (arg.Length == 0)
            return 2;
        if (!NeedsQuoting(arg))
            return arg.Length;

        var length = 2;
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            length += c == '"' ? backslashes * 2 + 2 : backslashes + 1;
            backslashes = 0;
        }

        return length + backslashes * 2;
    }

    /// <summary>
    /// Writes <paramref name="arg"/> with MSVCRT command-line quoting: backslashes double only before a quote
    /// or the closing quote, and an embedded quote is escaped. Returns the number of characters written.
    /// </summary>
    private static int WriteQuoted(Span<char> destination, string arg)
    {
        if (arg.Length == 0)
        {
            destination[0] = '"';
            destination[1] = '"';
            return 2;
        }

        if (!NeedsQuoting(arg))
        {
            arg.AsSpan().CopyTo(destination);
            return arg.Length;
        }

        var position = 0;
        destination[position++] = '"';
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                destination.Slice(position, backslashes * 2 + 1).Fill('\\');
                position += backslashes * 2 + 1;
            }
            else
            {
                destination.Slice(position, backslashes).Fill('\\');
                position += backslashes;
            }

            destination[position++] = c;
            backslashes = 0;
        }

        destination.Slice(position, backslashes * 2).Fill('\\');
        position += backslashes * 2;
        destination[position++] = '"';
        return position;
    }

    private static bool NeedsQuoting(string arg)
    {
        for (var i = 0; i < arg.Length; i++)
        {
            var c = arg[i];
            if (char.IsWhiteSpace(c) || c is '"' or '\\')
                return true;
        }

        return false;
    }

    /// <summary>
    /// The pseudo-console handle of the in-box ConPTY, which resizes and closes it through <c>kernel32.dll</c>. Every
    /// operation on a pseudo-console goes through the implementation that created it, so a handle from the out-of-band
    /// <c>conpty.dll</c> is an <see cref="OutOfBandPseudoConsoleHandle"/> instead; an in-box session carries nothing for
    /// the other implementation.
    /// </summary>
    private class SafePseudoConsoleHandle : SafeHandle
    {
        public SafePseudoConsoleHandle(IntPtr value) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(value);

        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <summary>Whether <see cref="Clear"/> does anything: the in-box ConPTY exposes no clear operation.</summary>
        public virtual bool CanClear => false;

        public virtual int Resize(WindowsCoord size) => WindowsInterop.ResizePseudoConsole(handle, size);

        public virtual int Clear() => 0;

        /// <summary>
        /// Drops MiniPty's reference to the console server once the child has attached, so the host exits with its
        /// last client and breaks the output pipe. Returns the HRESULT, or 0 without a call where this Windows build's
        /// <c>kernel32.dll</c> has no <c>ReleasePseudoConsole</c>.
        /// </summary>
        public virtual int ReleaseServerReference() =>
            WindowsInterop.CanReleasePseudoConsole ? WindowsInterop.ReleasePseudoConsole(handle) : 0;

        protected override bool ReleaseHandle()
        {
            WindowsInterop.ClosePseudoConsole(handle);
            return true;
        }
    }

    /// <summary>A pseudo-console created by an out-of-band ConPTY (<see cref="PtyStartInfo.ConPty"/>).</summary>
    private sealed class OutOfBandPseudoConsoleHandle : SafePseudoConsoleHandle
    {
        private readonly ConPtyLibrary _library;

        public OutOfBandPseudoConsoleHandle(IntPtr value, ConPtyLibrary library) : base(value) => _library = library;

        public override bool CanClear => true;

        public override int Resize(WindowsCoord size) => _library.ResizePseudoConsole(handle, size);

        // The frontend has cleared its own display, so the host's cursor goes to the top with it.
        public override int Clear() => _library.ClearPseudoConsole(handle, keepCursorRow: false);

        public override int ReleaseServerReference() => _library.ReleasePseudoConsole(handle);

        protected override bool ReleaseHandle()
        {
            _library.ClosePseudoConsole(handle);
            return true;
        }
    }

    /// <summary>
    /// A kernel object handle closed with <c>CloseHandle</c>: the child's process and, with
    /// <see cref="PtyStartInfo.KillProcessTree"/>, its job. See <c>WindowsPtyBackendInstance.TryPin</c>.
    /// </summary>
    private sealed class SafeKernelHandle : SafeHandle
    {
        public SafeKernelHandle(IntPtr value) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(value);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => WindowsInterop.CloseHandle(handle);
    }

}

internal static partial class WindowsInterop
{

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true)]
    internal static unsafe partial SafeFileHandle CreateNamedPipe(
        char* lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    internal static unsafe partial SafeFileHandle CreateFile(
        char* lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileCompletionNotificationModes(SafeFileHandle fileHandle, byte flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CreatePseudoConsole(
        WindowsCoord size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ResizePseudoConsole(IntPtr hPC, WindowsCoord size);

    [LibraryImport("kernel32.dll")]
    internal static partial void ClosePseudoConsole(IntPtr hPC);

    /// <summary>
    /// <c>HRESULT ReleasePseudoConsole(HPCON hPC)</c> from <c>kernel32.dll</c>, or null on a Windows build that does not
    /// export it; Windows 11 24H2 (build 26100) is the first that does. Resolved once, on first use, rather than
    /// imported: an import of a missing export fails each call with <c>EntryPointNotFoundException</c>, which would make
    /// every session on an older build pay for an exception.
    /// </summary>
    private static readonly unsafe delegate* unmanaged[Stdcall]<IntPtr, int> s_releasePseudoConsole = FindReleasePseudoConsole();

    /// <summary>Whether this Windows build's in-box ConPTY can release a pseudo-console's server reference.</summary>
    internal static unsafe bool CanReleasePseudoConsole => s_releasePseudoConsole != null;

    /// <summary>Calls <c>ReleasePseudoConsole</c>; only when <see cref="CanReleasePseudoConsole"/>.</summary>
    internal static unsafe int ReleasePseudoConsole(IntPtr hPC) => s_releasePseudoConsole(hPC);

    private static unsafe delegate* unmanaged[Stdcall]<IntPtr, int> FindReleasePseudoConsole()
    {
        // kernel32.dll is loaded in every Windows process and is a known DLL, so this binds the system copy and only
        // adds a reference to it. The reference is never dropped, as the function stays in use for the process.
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("kernel32.dll", out var kernel32))
            return null;

        return NativeLibrary.TryGetExport(kernel32, "ReleasePseudoConsole", out var address)
            ? (delegate* unmanaged[Stdcall]<IntPtr, int>)address
            : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessW(
        string? lpApplicationName,
        char[] lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref WindowsStartupInfoEx lpStartupInfo,
        out WindowsProcessInformation lpProcessInformation);
}
