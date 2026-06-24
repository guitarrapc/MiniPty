namespace MiniPty;

/// <summary>
/// A chunk of bytes read from a pseudo-terminal output stream.
/// </summary>
/// <remarks>
/// The memory referenced by <see cref="Data"/> is valid only until the next successful
/// <c>MoveNextAsync</c> call on the same output enumeration. Copy the data if it must be retained.
/// </remarks>
public readonly struct PtyOutputChunk
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PtyOutputChunk"/> struct.
    /// </summary>
    /// <param name="data">The output bytes for this chunk.</param>
    public PtyOutputChunk(ReadOnlyMemory<byte> data) => Data = data;

    /// <summary>
    /// Gets the output bytes for this chunk.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }
}