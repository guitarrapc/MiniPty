using System.Text;

namespace MiniPty.Internal;

internal readonly struct PtyPumpOutput(byte[] outputBytes, char[]? outputChars, Encoding encoding)
{
    internal byte[] OutputBytes { get; } = outputBytes;

    internal char[]? OutputChars { get; } = outputChars;

    internal Encoding Encoding { get; } = encoding;

    internal PtyPumpPayload ToPayload() => new(OutputBytes, OutputChars, Encoding);
}
