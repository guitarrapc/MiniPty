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

internal static class WindowsPtyBackend
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    // EOF staging advances one step per tick, matching the cadence of the exit-wait poll loop it replaced.
    private const int EofTickMs = 100;
    private const byte InputEofCtrlZ = 0x1A;
    private const byte InputEofSubmit = 0x0D;
    private const int EofDeferPollsEmptyInput = 40;
    // Value of the deferral count once the EOF chord was written. Counts never go below zero.
    private const int EofChordWrittenMarker = -1;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = WindowsInterop.ErrorNoData;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;

    // Why the input pipe closed, which decides how queued writes fail.
    private const int InputClosedByEof = 0;
    private const int InputOutputEnded = 1;
    private const int InputDisposed = 2;

    /// <summary>
    /// The input pipe's buffer, and the most one overlapped write carries. It keeps the anonymous pipe's default
    /// size.
    /// </summary>
    private const int InputPipeBufferSize = 4096;
    private const string InputPipeNamePrefix = @"\\.\pipe\minipty-input-";
    private const uint PipeAccessOutbound = 0x00000002;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    // PIPE_TYPE_BYTE, PIPE_READMODE_BYTE, and PIPE_WAIT are zero.
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityIdentification = 0x00010000;
    private const byte FileSkipCompletionPortOnSuccess = 0x1;
    private const byte FileSkipSetEventOnHandle = 0x2;
    /// <summary>
    /// How long a continuation read waits for the next ConPTY slice. ConPTY delivers bulk output in slices of up to
    /// about 1.7 KB that arrive 20–45 µs apart (median and 90th percentile measured on Windows 11), so this window
    /// joins them into full slots with ample margin, while output trickling in is published within it.
    /// </summary>
    /// <remarks>
    /// Measured with <see cref="System.Diagnostics.Stopwatch"/>, not <see cref="Environment.TickCount64"/>: a
    /// tick-count window lasts until the next system clock tick (up to about 16 ms), during which the reader spun
    /// a whole core for any session whose child wrote small pieces.
    /// </remarks>
    private const int CoalesceWindowMicroseconds = 500;
    private static readonly long s_coalesceWindowTicks = System.Diagnostics.Stopwatch.Frequency * CoalesceWindowMicroseconds / 1_000_000;
    /// <summary>
    /// Bounds the windows chained into one slot, as on Unix, so output trickling in faster than the window is
    /// published within about 2.5 ms instead of being held until the slot fills. Bulk output fills a slot within
    /// a few slices, long before the budget.
    /// </summary>
    private const int CoalesceBudgetMs = 2;
    private static readonly long s_coalesceBudgetTicks = System.Diagnostics.Stopwatch.Frequency * CoalesceBudgetMs / 1000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static IPtyBackend Start(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        CreateConPtyPipes(out var inputRead, out var inputWrite, out var outputRead, out var outputWrite);
        var inputWriteHandle = inputWrite;
        var outputReadHandle = outputRead;

        var size = startInfo.ClampedSize;
        var coord = new WindowsCoord((short)size.Columns, (short)size.Rows);
        var hr = WindowsInterop.CreatePseudoConsole(coord, inputRead, outputWrite, 0, out var pseudoConsoleHandle);
        if (hr < 0)
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        var hpc = new SafePseudoConsoleHandle(pseudoConsoleHandle);
        inputRead.Dispose();
        outputWrite.Dispose();

        var attrList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var processInfo = new WindowsProcessInformation();
        try
        {
            nuint attrListSize = 0;
            _ = WindowsInterop.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
            if (attrListSize == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList size query failed");

            attrList = Marshal.AllocHGlobal((IntPtr)(nint)attrListSize);
            if (!WindowsInterop.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");

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

            var commandLine = BuildCommandLine(startInfo);
            var environment = PtyEnvironment.BuildWindows(startInfo);
            var creationFlags = ExtendedStartupInfoPresent;
            if (environment is not null)
            {
                environmentBlock = AllocEnvironmentBlock(environment);
                creationFlags |= CreateUnicodeEnvironment;
            }

            if (!WindowsInterop.CreateProcessW(
                    null,
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

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
                environmentBlock = IntPtr.Zero;
            }

            return new WindowsPtyBackendInstance(
                inputWriteHandle,
                outputReadHandle,
                hpc,
                attrList,
                processInfo,
                size);
        }
        catch
        {
            if (processInfo.hThread != IntPtr.Zero)
                WindowsInterop.CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
                WindowsInterop.CloseHandle(processInfo.hProcess);
            if (attrList != IntPtr.Zero)
            {
                WindowsInterop.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }

            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);

            hpc.Dispose();
            inputWriteHandle.Dispose();
            outputReadHandle.Dispose();
            throw;
        }
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

        // MiniPty's end of the input pipe, opened overlapped (see CreateInputPipe).
        private readonly SafeFileHandle _inputWriteHandle;
        private readonly SafeFileHandle _outputReadHandle;
        private readonly SafePseudoConsoleHandle _hpc;
        private readonly IntPtr _attrList;
        private readonly WindowsProcessInformation _processInfo;
        // Mutable struct: used only through this field, never copied.
        private PtyExitSignal _exitSignal;
        private Timer? _eofTimer;
        private PtySize _size;
        private bool _inputClosed;
        private bool _outputClosed;
        private bool _eofSignaled;
        private bool _eofPending;
        /// <summary>
        /// Ticks left before an input pipe that never carried bytes is closed, or <see cref="EofChordWrittenMarker"/> once
        /// the Ctrl+Z / CR chord was written. One field holds both because another field would enlarge every session.
        /// </summary>
        private int _eofDeferPollsRemaining;

        private bool HasInputBytes => _input is { LastStagedByte: >= 0 };

        /// <summary>The Ctrl+Z / CR chord was written. The input pipe then stays open until the child exits.</summary>
        private bool EofChordWritten => _eofDeferPollsRemaining == EofChordWrittenMarker;
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
            WindowsProcessInformation processInfo,
            PtySize size)
        {
            _inputWriteHandle = inputWriteHandle;
            _outputReadHandle = outputReadHandle;
            _hpc = hpc;
            _attrList = attrList;
            _processInfo = processInfo;
            _size = size;
            Input = new PtyInputStream(this);

            // Last, so the watcher never observes a partially constructed instance.
            WindowsExitWatcher.Register(processInfo.hProcess, this);
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

        // Input is written by I/O completions on Windows, so the reader has nothing to write while it waits.
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
        /// releases the overlapped resources. An EOF close that finds bytes written leaves the pipe open and stages EOF
        /// again, to be sent as the chord.
        /// </summary>
        private void CloseInputPipe(int reason)
        {
            PtyPendingInput? queued = null;
            lock (this)
            {
                if (_inputClosed)
                    return;

                // A write can land between the unlocked check in CloseInputPipeIfEofSignaled and this lock. Closing the
                // pipe after bytes is not EOF to the child, so EOF is staged again and sent as the chord.
                if (reason == InputClosedByEof && HasInputBytes)
                {
                    _eofSignaled = false;
                    _eofPending = true;
                    return;
                }

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

        public void WaitWhileOutputBlocked(PtyReaderThread reader) => reader.WaitForWake(Timeout.Infinite);
        public int ProcessId => _processInfo.dwProcessId;

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

            columns = Math.Clamp(columns, 1, 512);
            rows = Math.Clamp(rows, 1, 512);

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
                hr = WindowsInterop.ResizePseudoConsole(
                    _hpc.DangerousGetHandle(),
                    new WindowsCoord((short)columns, (short)rows));
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

        public void SendEof()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState() || _inputClosed || _eofPending || EofChordWritten)
                return;

            if (_eofSignaled)
                return;

            // Stage EOF: WriteFile can succeed before ConPTY stdin is attached. The staging timer advances
            // it, so EOF no longer depends on somebody awaiting exit.
            _eofPending = true;
            _eofTimer ??= new Timer(static state => ((WindowsPtyBackendInstance)state!).OnEofTick(), this, Timeout.Infinite, Timeout.Infinite);
            _eofTimer.Change(EofTickMs, Timeout.Infinite);
        }

        public void Kill()
        {
            if (_disposed || TryRefreshExitState() || _processInfo.hProcess == IntPtr.Zero)
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

        private void KillCore()
        {
            WindowsInterop.TerminateProcess(_processInfo.hProcess, 1);
        }

        public void OnProcessExited()
        {
            if (!_disposed)
                TryRefreshExitState();
        }

        /// <summary>
        /// One EOF staging step, the work the exit-wait poll loop used to do on each 100 ms timeout:
        /// promote a staged EOF, then count down to closing an input pipe that never carried bytes.
        /// </summary>
        private void OnEofTick()
        {
            if (_disposed || _inputClosed || TryRefreshExitState())
                return;

            // A timer callback must never throw: an unhandled exception there terminates the host process.
            try
            {
                PromoteEofIfPending();
                CloseInputPipeIfEofSignaled();
                if (!_disposed && !_inputClosed && (_eofPending || (_eofSignaled && !EofChordWritten)))
                    _eofTimer?.Change(EofTickMs, Timeout.Infinite);
            }
            catch (Exception)
            {
                // The input pipe failed, or Dispose closed it or the timer under the tick; nothing is left to signal.
            }
        }

        /// <summary>
        /// Closes the pseudo-console. ConPTY then flushes what it has rendered and closes its end of the output
        /// pipe, so the reader drains the rest and sees end of output. The output pipe itself stays open for that
        /// drain and closes with <see cref="Dispose"/>.
        /// </summary>
        public void CloseOutputTransport()
        {
            if (!_hpcClosed)
                CloseTransport(InputOutputEnded);
        }

        public bool IsOutputClosed => Volatile.Read(ref _outputClosed);

        public long CoalesceBudgetTicks => s_coalesceBudgetTicks;

        // The output pipe handle is pinned by each ReadFile call (SafeHandle reference counting), so a concurrent
        // Dispose defers the close until the read returns, and later reads see ObjectDisposedException.
        public bool TryBeginOutputRead() => true;

        public void EndOutputRead()
        {
        }

        /// <summary>
        /// Blocking <c>ReadFile</c> on the ConPTY output pipe. Never woken early: closing the pseudo-console ends
        /// it, and the drain closes it after exit.
        /// </summary>
        public unsafe int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            try
            {
                fixed (byte* ptr = buffer)
                {
                    if (WindowsInterop.ReadFile(_outputReadHandle, ptr, (uint)buffer.Length, out var read, IntPtr.Zero))
                        return (int)read;
                }
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorBrokenPipe or ErrorInvalidHandle)
                return 0;

            throw new IOException($"ReadFile failed (Win32 {error})");
        }

        /// <summary>
        /// Appends output that is available now or arrives within the micro-window. ConPTY delivers bulk output in
        /// small slices shortly after one another, and <c>PeekNamedPipe</c> often reports zero bytes on its pipes
        /// while more is in flight, so this reads with <c>PIPE_NOWAIT</c> and retries until the window ends. The
        /// anonymous pipe cannot be waited on, so the window is a short yield loop.
        /// </summary>
        public int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            var read = TryReadOutputNow(buffer, out endOfOutput);
            if (read > 0 || endOfOutput)
                return read;

            var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + s_coalesceWindowTicks;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(0);
                read = TryReadOutputNow(buffer, out endOfOutput);
                if (read > 0 || endOfOutput)
                    return read;
            }

            return 0;
        }

        private unsafe int TryReadOutputNow(Span<byte> buffer, out bool endOfOutput)
        {
            endOfOutput = false;
            if (_outputReadHandle.IsClosed)
            {
                endOfOutput = true;
                return 0;
            }

            uint nowait = WindowsInterop.PipeNowait;
            try
            {
                if (!WindowsInterop.SetNamedPipeHandleState(_outputReadHandle, &nowait, IntPtr.Zero, IntPtr.Zero))
                    return 0;

                try
                {
                    fixed (byte* ptr = buffer)
                    {
                        if (WindowsInterop.ReadFile(_outputReadHandle, ptr, (uint)buffer.Length, out var read, IntPtr.Zero))
                            return (int)read;
                    }

                    var error = Marshal.GetLastPInvokeError();
                    if (error == WindowsInterop.ErrorNoData)
                        return 0;

                    if (error is ErrorBrokenPipe or ErrorInvalidHandle)
                    {
                        endOfOutput = true;
                        return 0;
                    }

                    throw new IOException($"ReadFile failed (Win32 {error})");
                }
                finally
                {
                    uint wait = WindowsInterop.PipeWait;
                    _ = WindowsInterop.SetNamedPipeHandleState(_outputReadHandle, &wait, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (ObjectDisposedException)
            {
                endOfOutput = true;
                return 0;
            }
        }

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

            if (_processInfo.hThread != IntPtr.Zero)
                WindowsInterop.CloseHandle(_processInfo.hThread);
            if (_processInfo.hProcess != IntPtr.Zero)
                WindowsInterop.CloseHandle(_processInfo.hProcess);
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

        private bool TryRefreshExitState()
        {
            if (_exited)
                return true;

            if (_processInfo.hProcess == IntPtr.Zero)
                return false;

            var waitResult = WindowsInterop.WaitForSingleObject(_processInfo.hProcess, 0);
            if (waitResult == WindowsInterop.WaitTimeout)
                return false;
            if (waitResult == WindowsInterop.WaitFailed)
                return false;

            if (!WindowsInterop.GetExitCodeProcess(_processInfo.hProcess, out var exitCode))
                return false;

            _exitCode = unchecked((int)exitCode);
            _exited = true;
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

                if (HasInputBytes)
                {
                    // EOF goes after every byte written before it: while input is queued or in flight, a later tick
                    // sends it.
                    if (!IsInputIdleLocked && !_inputClosed)
                        return;

                    // ConPTY input pipe close is observed as STATUS_CONTROL_C_EXIT, not EOF.
                    // Legacy console EOF is Ctrl+Z submitted with CR; keep the pipe open until exit.
                    _eofPending = false;
                    WriteStreamEofToInputLocked();
                    _eofSignaled = true;
                    return;
                }

                _eofPending = false;
                _eofSignaled = true;
                _eofDeferPollsRemaining = EofDeferPollsEmptyInput;
            }
        }

        private void CloseInputPipeIfEofSignaled()
        {
            if (!_eofSignaled || EofChordWritten)
                return;

            // Input that never carried bytes ends by closing the pipe after a wait. Bytes written during the wait rule
            // that out, because closing the pipe after bytes is not EOF to the child, so EOF is sent as the chord.
            if (HasInputBytes)
            {
                _eofSignaled = false;
                _eofPending = true;
                return;
            }

            if (_eofDeferPollsRemaining > 0)
            {
                _eofDeferPollsRemaining--;
                return;
            }

            CloseInputPipe(InputClosedByEof);
        }

        /// <summary>
        /// Writes the EOF chord through the staging buffer; a write left pending completes on its own. Caller holds
        /// lock (this) and has checked that nothing is queued.
        /// </summary>
        private void WriteStreamEofToInputLocked()
        {
            if (_inputClosed || EofChordWritten)
                return;

            // Ctrl+Z alone does not end input on ConPTY. A CR submits the EOF key chord. Ctrl+Z ends input only at the
            // start of a line, so a pending line is submitted first.
            var input = EnsureInputLocked();
            Span<byte> chord = stackalloc byte[3];
            var length = 0;
            if (input.LastStagedByte is not '\n' and not '\r')
                chord[length++] = InputEofSubmit;

            chord[length++] = InputEofCtrlZ;
            chord[length++] = InputEofSubmit;
            StageLocked(input, chord[..length], owner: null);
            _eofDeferPollsRemaining = EofChordWrittenMarker;
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
                CloseInputPipeIfEofSignaled();
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
    /// Creates the ConPTY pipes: an anonymous output pipe, which the session's reader thread reads with blocking
    /// reads, and an input pipe whose MiniPty end is overlapped (see <see cref="CreateInputPipe"/>).
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
        CreateInputPipe(out inputRead, out inputWrite);
        if (!WindowsInterop.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            outputRead.Dispose();
            outputWrite.Dispose();
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(error, "CreatePipe failed");
        }
    }

    /// <summary>
    /// Creates the input pipe. MiniPty's end is the server end of a named pipe opened overlapped, because an
    /// anonymous pipe cannot be. A write that ConPTY is not taking yet then waits without blocking a thread.
    /// ConPTY's end is opened synchronously by name, as ConPTY reads it with blocking reads.
    /// </summary>
    /// <remarks>
    /// The name is random; the pipe allows one instance, must be the first instance of that name, and rejects remote
    /// clients. If anything else connects first, opening ConPTY's end fails and so does the spawn, instead of sharing
    /// the pipe. The buffer keeps the anonymous pipe's default size; with none, every write would wait for ConPTY to
    /// read it.
    /// </remarks>
    private static unsafe void CreateInputPipe(out SafeFileHandle conPtySide, out SafeFileHandle ourSide)
    {
        // Prefix, 32 hexadecimal digits, and the terminating NUL.
        Span<char> name = stackalloc char[InputPipeNamePrefix.Length + 33];
        InputPipeNamePrefix.CopyTo(name);
        Guid.NewGuid().TryFormat(name[InputPipeNamePrefix.Length..], out var digits, "N");
        name[InputPipeNamePrefix.Length + digits] = '\0';

        fixed (char* path = name)
        {
            ourSide = WindowsInterop.CreateNamedPipe(
                path,
                PipeAccessOutbound | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients,
                1,
                InputPipeBufferSize,
                0,
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
                GenericRead,
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
                throw new Win32Exception(error, "Opening the ConPTY end of the input pipe failed");
            }
        }
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

    private sealed class SafePseudoConsoleHandle : SafeHandle
    {
        public SafePseudoConsoleHandle(IntPtr value) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(value);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            WindowsInterop.ClosePseudoConsole(handle);
            return true;
        }
    }

}

internal static partial class WindowsInterop
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

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
