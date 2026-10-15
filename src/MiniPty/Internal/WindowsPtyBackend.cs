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

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsSecurityAttributes
{
    public int nLength;
    public IntPtr lpSecurityDescriptor;
    [MarshalAs(UnmanagedType.Bool)]
    public bool bInheritHandle;
}

internal static class WindowsPtyBackend
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    // EOF staging advances one step per tick, matching the cadence of the exit-wait poll loop it replaced.
    private const int EofTickMs = 100;
    private const uint HandleFlagInherit = 0x00000001;
    private const byte InputEofCtrlZ = 0x1A;
    private const byte InputEofSubmit = 0x0D;
    /// <summary>Sentinel for <see cref="WindowsPtyBackendInstance._inputTailByte"/> when no stdin bytes were written.</summary>
    private const byte InputTailUnset = 0xFF;
    private const int EofDeferPollsEmptyInput = 40;
    private const uint CreateUnicodeEnvironment = 0x00000400;

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
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

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

    private sealed class WindowsPtyBackendInstance : IPtyBackend, IWindowsExitObserver, IValueTaskSource<int>
    {
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
        /// <summary>Last stdin byte written, or <see cref="InputTailUnset"/> when none. Avoids a separate written flag.</summary>
        private byte _inputTailByte = InputTailUnset;
        private int _eofDeferPollsRemaining;

        private bool HasInputBytes => _inputTailByte != InputTailUnset;

        /// <summary>Stream Ctrl+Z EOF was written; input pipe must stay open until child exit.</summary>
        private bool StreamEofSignaled => _eofSignaled && HasInputBytes;
        private bool _hpcClosed;
        private bool _exited;
        private int _exitCode;
        private bool _disposed;

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
            Input = new InputTrackingWriteStream(new PtyHandleWriteStream(inputWriteHandle), OnInputWritten);
            Output = new PtyHandleReadStream(outputReadHandle);

            // Last, so the watcher never observes a partially constructed instance.
            WindowsExitWatcher.Register(processInfo.hProcess, this);
        }

        public Stream Input { get; }
        public Stream Output { get; }
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
            if (TryRefreshExitState() || _inputClosed || _eofPending || StreamEofSignaled)
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

        public void CloseTransportAfterExit()
        {
            if (!_disposed && TryRefreshExitState())
                CloseTransport();
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
                if (!_disposed && !_inputClosed && (_eofPending || (_eofSignaled && !StreamEofSignaled)))
                    _eofTimer?.Change(EofTickMs, Timeout.Infinite);
            }
            catch (Exception)
            {
                // The input pipe failed, or Dispose closed it or the timer under the tick; nothing is left to signal.
            }
        }

        public void CloseOutputTransport()
        {
            if (!_hpcClosed)
                CloseTransport();

            CloseOutputPipe();
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

            CloseTransport();
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

            if (!_inputClosed)
            {
                _inputWriteHandle.Dispose();
                _inputClosed = true;
            }

            if (!_outputClosed)
            {
                _outputReadHandle.Dispose();
                _outputClosed = true;
            }
        }

        private void OnInputWritten(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
                return;

            _inputTailByte = buffer[^1];
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
            if (!_eofPending)
                return;

            _eofPending = false;
            if (HasInputBytes)
            {
                // ConPTY input pipe close is observed as STATUS_CONTROL_C_EXIT, not EOF.
                // Legacy console EOF is Ctrl+Z submitted with CR; keep the pipe open until exit.
                WriteStreamEofToInput();
                _eofSignaled = true;
                return;
            }

            _eofSignaled = true;
            _eofDeferPollsRemaining = EofDeferPollsEmptyInput;
        }

        private void CloseInputPipeIfEofSignaled()
        {
            if (!_eofSignaled || StreamEofSignaled)
                return;

            if (_eofDeferPollsRemaining > 0)
            {
                _eofDeferPollsRemaining--;
                return;
            }

            CloseInputPipe();
        }

        private void WriteStreamEofToInput()
        {
            if (_inputClosed || StreamEofSignaled)
                return;

            // Ctrl+Z alone does not end input on ConPTY; CR submits the EOF key chord.
            // When the caller buffer does not end with a line terminator, submit the pending line first.
            if (_inputTailByte is not (byte)'\n' and not (byte)'\r')
                PtyIo.WriteAll(_inputWriteHandle, stackalloc byte[1] { InputEofSubmit });

            PtyIo.WriteAll(_inputWriteHandle, stackalloc byte[2] { InputEofCtrlZ, InputEofSubmit });
        }

        private void CloseInputPipe()
        {
            if (_inputClosed)
                return;

            // Best-effort; named pipes often return ERROR_INVALID_FUNCTION — safe to ignore.
            _ = WindowsInterop.FlushFileBuffers(_inputWriteHandle);
            _inputWriteHandle.Dispose();
            _inputClosed = true;
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

        private void CloseTransport()
        {
            PromoteEofIfPending();
            CloseInputPipeIfEofSignaled();
            if (!_inputClosed)
            {
                _inputWriteHandle.Dispose();
                _inputClosed = true;
            }

            if (!_hpcClosed)
            {
                _hpc.Dispose();
                _hpcClosed = true;
            }
        }
    }

    private static void CreateConPtyPipes(
        out SafeFileHandle inputRead,
        out SafeFileHandle inputWrite,
        out SafeFileHandle outputRead,
        out SafeFileHandle outputWrite)
    {
        var securityAttributes = new WindowsSecurityAttributes
        {
            nLength = Marshal.SizeOf<WindowsSecurityAttributes>(),
            bInheritHandle = true,
        };
        var attrPtr = Marshal.AllocHGlobal(securityAttributes.nLength);
        try
        {
            Marshal.StructureToPtr(securityAttributes, attrPtr, false);
            if (!WindowsInterop.CreatePipe(out inputRead, out inputWrite, attrPtr, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed");
            if (!WindowsInterop.CreatePipe(out outputRead, out outputWrite, attrPtr, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed");
        }
        finally
        {
            Marshal.FreeHGlobal(attrPtr);
        }

        if (!WindowsInterop.SetHandleInformation(inputWrite, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed");
        if (!WindowsInterop.SetHandleInformation(outputRead, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed");
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

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
