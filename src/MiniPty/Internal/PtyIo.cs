using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

internal static class PtyIo
{
    internal const int Utf8StackThreshold = 256;

    internal static unsafe void WriteAll(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        fixed (byte* ptr = bytes)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var remaining = (uint)(bytes.Length - offset);
                if (!WindowsInterop.WriteFile(handle, ptr + offset, remaining, out var written, IntPtr.Zero))
                    throw new IOException($"WriteFile failed (Win32 {Marshal.GetLastPInvokeError()})");
                if (written == 0)
                    throw new IOException("WriteFile wrote 0 bytes");
                offset += (int)written;
            }
        }
    }

    internal static unsafe void WriteAll(int fd, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        fixed (byte* ptr = bytes)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var written = UnixInterop.Write(fd, ptr + offset, (nuint)(bytes.Length - offset));
                if (written < 0)
                {
                    if (Marshal.GetLastPInvokeError() == UnixInterop.EINTR)
                        continue;
                    throw new IOException($"write failed (errno {Marshal.GetLastPInvokeError()})");
                }

                if (written == 0)
                    throw new IOException("write wrote 0 bytes");
                offset += written;
            }
        }
    }

    internal static void WriteUtf8(SafeFileHandle handle, string input)
    {
        var byteCount = Encoding.UTF8.GetByteCount(input);
        if (byteCount == 0)
            return;

        if (byteCount <= Utf8StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(input, buffer);
            WriteAll(handle, buffer);
            return;
        }

        WriteAll(handle, Encoding.UTF8.GetBytes(input));
    }

    internal static void WriteUtf8(int fd, string input)
    {
        var byteCount = Encoding.UTF8.GetByteCount(input);
        if (byteCount == 0)
            return;

        if (byteCount <= Utf8StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(input, buffer);
            WriteAll(fd, buffer);
            return;
        }

        WriteAll(fd, Encoding.UTF8.GetBytes(input));
    }

    internal static Task WriteTextAsync(
        Stream stream,
        string text,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var byteCount = encoding.GetByteCount(text);
        if (byteCount == 0)
            return Task.CompletedTask;

        if (byteCount <= Utf8StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            encoding.GetBytes(text, buffer);
            stream.Write(buffer);
            return Task.CompletedTask;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = encoding.GetBytes(text, rented.AsSpan(0, byteCount));
            stream.Write(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return Task.CompletedTask;
    }

    internal static Task WriteUtf8Async(Stream stream, string input, CancellationToken cancellationToken)
    {
        var byteCount = Encoding.UTF8.GetByteCount(input);
        if (byteCount == 0)
            return Task.CompletedTask;

        if (byteCount <= Utf8StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(input, buffer);
            stream.Write(buffer);
            return Task.CompletedTask;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(input, rented.AsSpan(0, byteCount));
            stream.Write(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return Task.CompletedTask;
    }

    internal static int ToWaitMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return 0;
        if (timeout >= TimeSpan.FromMilliseconds(int.MaxValue))
            return int.MaxValue;
        return (int)timeout.TotalMilliseconds;
    }
}
