using System.Collections;

namespace MiniPty.Internal;

internal static class PtyEnvironment
{
    private const string DefaultUnixTerm = "xterm-256color";

    private static readonly string[] UnixSanitizedKeys =
    [
        "TMUX",
        "TMUX_PANE",
        "STY",
        "WINDOW",
        "WINDOWID",
        "TERMCAP",
        "COLUMNS",
        "LINES",
    ];

    /// <summary>Builds the Unix child environment from <paramref name="startInfo"/> and an optional parent snapshot.</summary>
    /// <param name="startInfo">Spawn options containing overlay and terminal name.</param>
    /// <param name="parentEnvironment">
    /// Optional parent-environment snapshot for tests. When <see langword="null"/>, the current process environment is used.
    /// </param>
    public static KeyValuePair<string, string>[]? BuildUnix(
        PtyStartInfo startInfo,
        IReadOnlyDictionary<string, string>? parentEnvironment = null)
    {
        ValidateTerminalName(startInfo.TerminalName);

        if (startInfo.Environment is null && string.IsNullOrEmpty(startInfo.TerminalName))
            return null;

        var entries = CreateParentMap(StringComparer.Ordinal, parentEnvironment);
        for (var i = 0; i < UnixSanitizedKeys.Length; i++)
            entries.Remove(UnixSanitizedKeys[i]);

        var termTouched = ApplyOverlay(entries, startInfo.Environment, StringComparer.Ordinal, "TERM");
        if (!string.IsNullOrEmpty(startInfo.TerminalName))
        {
            entries["TERM"] = new EnvironmentEntry("TERM", startInfo.TerminalName);
        }
        else if (!termTouched && !entries.ContainsKey("TERM"))
        {
            entries["TERM"] = new EnvironmentEntry("TERM", DefaultUnixTerm);
        }

        return ToArray(entries);
    }

    public static KeyValuePair<string, string>[]? BuildWindows(
        PtyStartInfo startInfo,
        IReadOnlyDictionary<string, string>? parentEnvironment = null)
    {
        ValidateTerminalName(startInfo.TerminalName);
        if (startInfo.Environment is null)
            return null;

        var entries = CreateParentMap(StringComparer.OrdinalIgnoreCase, parentEnvironment);
        ApplyOverlay(entries, startInfo.Environment, StringComparer.OrdinalIgnoreCase, termKey: null);
        return ToArray(entries);
    }

    private static Dictionary<string, EnvironmentEntry> CreateParentMap(
        StringComparer comparer,
        IReadOnlyDictionary<string, string>? parentEnvironment = null)
    {
        var entries = new Dictionary<string, EnvironmentEntry>(comparer);
        if (parentEnvironment is null)
        {
            var parent = System.Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry entry in parent)
            {
                if (entry.Key is not string key || entry.Value is not string value)
                    continue;

                entries[key] = new EnvironmentEntry(key, value);
            }

            return entries;
        }

        foreach (var pair in parentEnvironment)
            entries[pair.Key] = new EnvironmentEntry(pair.Key, pair.Value);

        return entries;
    }

    private static bool ApplyOverlay(
        Dictionary<string, EnvironmentEntry> entries,
        IReadOnlyDictionary<string, string?>? overlay,
        StringComparer comparer,
        string? termKey)
    {
        if (overlay is null)
            return false;

        var termTouched = false;
        foreach (var pair in overlay)
        {
            ValidateEnvironmentVariable(pair.Key, pair.Value);
            if (termKey is not null && comparer.Equals(pair.Key, termKey))
                termTouched = true;

            if (pair.Value is null)
                entries.Remove(pair.Key);
            else
                entries[pair.Key] = new EnvironmentEntry(pair.Key, pair.Value);
        }

        return termTouched;
    }

    private static KeyValuePair<string, string>[] ToArray(Dictionary<string, EnvironmentEntry> entries)
    {
        var result = new KeyValuePair<string, string>[entries.Count];
        var index = 0;
        foreach (var entry in entries.Values)
            result[index++] = new KeyValuePair<string, string>(entry.Name, entry.Value);
        return result;
    }

    private static void ValidateEnvironmentVariable(string key, string? value)
    {
        if (key.Length == 0)
            throw new ArgumentException("Environment variable names cannot be empty.", nameof(PtyStartInfo.Environment));
        if (key.Contains('=') || key.Contains('\0'))
            throw new ArgumentException("Environment variable names cannot contain '=' or NUL.", nameof(PtyStartInfo.Environment));
        if (value is not null && value.Contains('\0'))
            throw new ArgumentException("Environment variable values cannot contain NUL.", nameof(PtyStartInfo.Environment));
    }

    private static void ValidateTerminalName(string? terminalName)
    {
        if (terminalName is not null && terminalName.Contains('\0'))
            throw new ArgumentException("TerminalName cannot contain NUL.", nameof(PtyStartInfo.TerminalName));
    }

    private readonly record struct EnvironmentEntry(string Name, string Value);
}
