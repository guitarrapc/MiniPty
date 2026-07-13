namespace MiniPty.Terminal;

/// <summary>
/// Credentials used to attach to a persistent bridge-managed terminal session.
/// </summary>
/// <param name="SessionId">Opaque session identifier suitable for routing.</param>
/// <param name="AuthenticationToken">Secret 256-bit bearer token. Do not log or persist it in plaintext.</param>
public readonly record struct PtyBridgeSessionCredentials(Guid SessionId, string AuthenticationToken)
{
    /// <summary>Returns a diagnostic representation with the bearer token redacted.</summary>
    /// <returns>Session id and a redacted token marker.</returns>
    public override string ToString() =>
        $"{nameof(PtyBridgeSessionCredentials)} {{ {nameof(SessionId)} = {SessionId}, {nameof(AuthenticationToken)} = *** }}";
}
