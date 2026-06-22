namespace MiniPty.Internal;

internal readonly struct PtyPumpOutput(byte[] outputBytes, char[] outputChars)
{
    internal byte[] OutputBytes { get; } = outputBytes;

    internal char[] OutputChars { get; } = outputChars;

    internal ReadOnlyMemory<byte> Bytes => OutputBytes;

    internal ReadOnlyMemory<char> Chars => OutputChars;
}
