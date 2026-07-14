using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
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
    private const uint WaitPollMs = 100;
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

            var commandLineBuilder = new StringBuilder();
            commandLineBuilder.Append(QuoteArg(startInfo.FileName));
            if (startInfo.CommandLine is { Length: > 0 } rawCommandLine)
            {
                commandLineBuilder.Append(' ');
                commandLineBuilder.Append(rawCommandLine);
            }
            else
            {
                var arguments = startInfo.Arguments;
                for (var i = 0; i < arguments.Count; i++)
                {
                    commandLineBuilder.Append(' ');
                    commandLineBuilder.Append(QuoteArg(arguments[i]));
                }
            }

            var commandLine = (commandLineBuilder.ToString() + '\0').ToCharArray();
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

    private sealed class WindowsPtyBackendInstance : IPtyBackend
    {
        private readonly SafeFileHandle _inputWriteHandle;
        private readonly SafeFileHandle _outputReadHandle;
        private readonly SafePseudoConsoleHandle _hpc;
        private readonly IntPtr _attrList;
        private readonly WindowsProcessInformation _processInfo;
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
        }

        public Stream Input { get; }
        public Stream Output { get; }
        public int ProcessId => _processInfo.dwProcessId;

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
            if (_hpcClosed)
                throw new InvalidOperationException("Cannot resize after the pseudo-console has been closed.");

            columns = Math.Clamp(columns, 1, 512);
            rows = Math.Clamp(rows, 1, 512);
            var hr = WindowsInterop.ResizePseudoConsole(
                _hpc.DangerousGetHandle(),
                new WindowsCoord((short)columns, (short)rows));
            if (hr < 0)
                throw new Win32Exception(hr, "ResizePseudoConsole failed");

            _size = new PtySize(columns, rows);
        }

        public void SendEof()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState() || _inputClosed || _eofPending || StreamEofSignaled)
                return;

            if (_eofSignaled)
                return;

            // Stage EOF to the wait loop. WriteFile can succeed before ConPTY stdin is attached.
            _eofPending = true;
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

        public void PollForChildExitUntilExited(CancellationToken cancellationToken, bool closeTransportOnExit)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState())
            {
                if (closeTransportOnExit)
                    CloseTransport();

                ObjectDisposedException.ThrowIf(_disposed, this);
                return;
            }

            while (!TryRefreshExitState())
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();

                var waitResult = WindowsInterop.WaitForSingleObject(_processInfo.hProcess, WaitPollMs);
                PromoteEofIfPending();
                CloseInputPipeIfEofSignaled();
                if (waitResult == WindowsInterop.WaitFailed)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "WaitForSingleObject failed");

                if (waitResult != WindowsInterop.WaitObject0 && waitResult != WindowsInterop.WaitTimeout)
                    throw new InvalidOperationException($"WaitForSingleObject returned unexpected code 0x{waitResult:X8}");
            }

            if (closeTransportOnExit)
                CloseTransport();

            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit = true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState())
            {
                if (closeTransportOnExit)
                    CloseTransport();

                return Task.FromResult(_exitCode);
            }

            CancellationTokenRegistration registration = default;
            if (killOnCancellation)
            {
                registration = cancellationToken.Register(static state =>
                {
                    var backend = (WindowsPtyBackendInstance)state!;
                    backend.Kill();
                }, this);
            }

            try
            {
                PollForChildExitUntilExited(cancellationToken, closeTransportOnExit);
                return Task.FromResult(_exitCode);
            }
            finally
            {
                registration.Dispose();
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
            if (!TryRefreshExitState())
                KillCore();

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

    private static string QuoteArg(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";
        if (!NeedsQuoting(arg))
            return arg;

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
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
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
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
