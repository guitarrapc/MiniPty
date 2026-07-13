using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniPty.Terminal.Internal;

/// <summary>
/// Flat control-message DTO for the WebSocket bridge protocol. One shape covers every message
/// type; unused members stay null and are omitted on write. Unknown <see cref="Type"/> values are
/// ignored by the receiver for forward compatibility.
/// </summary>
internal sealed class BridgeMessage
{
    public string? Type { get; set; }
    public int? Cols { get; set; }
    public int? Rows { get; set; }
    public long? Bytes { get; set; }
    public int? ExitCode { get; set; }
    public int? Signal { get; set; }
}

/// <summary>
/// Source-generated JSON context so control-message parsing stays reflection-free under
/// NativeAOT and trimming.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BridgeMessage))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext
{
}

internal static class BridgeJson
{
    internal const string TypeResize = "resize";
    internal const string TypeAck = "ack";
    internal const string TypeExit = "exit";

    /// <summary>
    /// Parses a control message. Returns <see langword="false"/> on malformed JSON; the caller
    /// treats that as a protocol violation.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out BridgeMessage? message)
    {
        try
        {
            message = JsonSerializer.Deserialize(utf8Json, BridgeJsonContext.Default.BridgeMessage);
            return message is not null;
        }
        catch (JsonException)
        {
            message = null;
            return false;
        }
    }

    public static byte[] SerializeExit(PtyExitStatus status) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new BridgeMessage { Type = TypeExit, ExitCode = status.ExitCode, Signal = status.Signal },
            BridgeJsonContext.Default.BridgeMessage);
}
