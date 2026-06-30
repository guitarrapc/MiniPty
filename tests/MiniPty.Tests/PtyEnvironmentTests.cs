using MiniPty.Internal;

namespace MiniPty.Tests;

public sealed class PtyEnvironmentTests
{
    private static PtyStartInfo BaseStartInfo() => new() { FileName = "sh", Arguments = ["-c", "true"] };

    [Test]
    public async Task BuildUnix_ReturnsNullWhenOverlayAndTerminalNameAreAbsent()
    {
        var result = PtyEnvironment.BuildUnix(BaseStartInfo());

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildWindows_ReturnsNullWhenOverlayIsAbsent()
    {
        var result = PtyEnvironment.BuildWindows(BaseStartInfo() with { TerminalName = "xterm-test" });

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildUnix_OverlayOverridesAndInheritsParent()
    {
        const string parentKey = "MINIPTY_TEST_PARENT_ENV";
        const string overlayKey = "MINIPTY_TEST_OVERLAY_ENV";
        var parent = new Dictionary<string, string>
        {
            [parentKey] = "parent-value",
            [overlayKey] = "stale-overlay",
            ["PATH"] = "/bin",
        };

        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with { Environment = new Dictionary<string, string?> { [overlayKey] = "overlay-value" } },
            parent);

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, parentKey, out var parentValue)).IsTrue();
        await Assert.That(parentValue).IsEqualTo("parent-value");
        await Assert.That(TryGetValue(result!, overlayKey, out var overlayValue)).IsTrue();
        await Assert.That(overlayValue).IsEqualTo("overlay-value");
    }

    [Test]
    public async Task BuildUnix_NullOverlayRemovesAndEmptyOverlaySetsEmpty()
    {
        const string emptyKey = "MINIPTY_TEST_EMPTY_ENV";
        const string removeKey = "MINIPTY_TEST_REMOVE_ENV";
        var parent = new Dictionary<string, string>
        {
            [emptyKey] = "parent-empty",
            [removeKey] = "remove-me",
            ["PATH"] = "/bin",
        };

        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with
            {
                Environment = new Dictionary<string, string?> { [emptyKey] = string.Empty, [removeKey] = null }
            },
            parent);

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, emptyKey, out var emptyValue)).IsTrue();
        await Assert.That(emptyValue).IsEqualTo(string.Empty);
        await Assert.That(ContainsKey(result!, removeKey)).IsFalse();
    }

    [Test]
    public async Task BuildUnix_TerminalNameSetsTerm()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with
            {
                TerminalName = "xterm-test",
                Environment = new Dictionary<string, string?>()
            },
            new Dictionary<string, string> { ["PATH"] = "/bin" });

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, "TERM", out var term)).IsTrue();
        await Assert.That(term).IsEqualTo("xterm-test");
    }

    [Test]
    public async Task BuildUnix_DefaultTermIsAppliedWhenParentHasNoTerm()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with { Environment = new Dictionary<string, string?>() },
            new Dictionary<string, string> { ["PATH"] = "/bin" });

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, "TERM", out var term)).IsTrue();
        await Assert.That(term).IsEqualTo("xterm-256color");
    }

    [Test]
    public async Task BuildUnix_ExplicitTermRemovalSuppressesDefault()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with { Environment = new Dictionary<string, string?> { ["TERM"] = null } },
            new Dictionary<string, string> { ["PATH"] = "/bin", ["TERM"] = "parent-term" });

        await Assert.That(result).IsNotNull();
        await Assert.That(ContainsKey(result!, "TERM")).IsFalse();
    }

    [Test]
    public async Task BuildUnix_SanitizesInheritedTerminalSizeVariables()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with { Environment = new Dictionary<string, string?>() },
            new Dictionary<string, string>
            {
                ["TMUX_PANE"] = "%1",
                ["STY"] = "screen",
                ["WINDOW"] = "1",
                ["WINDOWID"] = "12345",
                ["TERMCAP"] = "termcap",
                ["COLUMNS"] = "999",
                ["LINES"] = "888",
                ["TMUX"] = "tmux",
                ["MINIPTY_CWD"] = "/tmp/stale",
                ["PATH"] = "/bin",
                ["MINIPTY_TEST_KEEP_ENV"] = "keep-me",
            });

        await Assert.That(result).IsNotNull();
        await Assert.That(ContainsKey(result!, "TMUX_PANE")).IsFalse();
        await Assert.That(ContainsKey(result!, "STY")).IsFalse();
        await Assert.That(ContainsKey(result!, "WINDOW")).IsFalse();
        await Assert.That(ContainsKey(result!, "WINDOWID")).IsFalse();
        await Assert.That(ContainsKey(result!, "TERMCAP")).IsFalse();
        await Assert.That(ContainsKey(result!, "COLUMNS")).IsFalse();
        await Assert.That(ContainsKey(result!, "LINES")).IsFalse();
        await Assert.That(ContainsKey(result!, "TMUX")).IsFalse();
        await Assert.That(ContainsKey(result!, "MINIPTY_CWD")).IsFalse();
        await Assert.That(TryGetValue(result!, "PATH", out var path)).IsTrue();
        await Assert.That(path).IsEqualTo("/bin");
        await Assert.That(TryGetValue(result!, "MINIPTY_TEST_KEEP_ENV", out var keepValue)).IsTrue();
        await Assert.That(keepValue).IsEqualTo("keep-me");
    }

    [Test]
    public async Task BuildUnix_OverlayTermWinsWhenTerminalNameIsAbsent()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with { Environment = new Dictionary<string, string?> { ["TERM"] = "overlay-term" } },
            new Dictionary<string, string> { ["PATH"] = "/bin", ["TERM"] = "parent-term" });

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, "TERM", out var term)).IsTrue();
        await Assert.That(term).IsEqualTo("overlay-term");
    }

    [Test]
    public async Task BuildUnix_TerminalNameWinsOverOverlayTerm()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with
            {
                TerminalName = "terminal-name-term",
                Environment = new Dictionary<string, string?> { ["TERM"] = "overlay-term" }
            },
            new Dictionary<string, string> { ["PATH"] = "/bin", ["TERM"] = "parent-term" });

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, "TERM", out var term)).IsTrue();
        await Assert.That(term).IsEqualTo("terminal-name-term");
    }

    [Test]
    public async Task BuildUnix_OverlayCanRestoreSanitizedKeys()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with { Environment = new Dictionary<string, string?> { ["COLUMNS"] = "120" } },
            new Dictionary<string, string> { ["COLUMNS"] = "999", ["PATH"] = "/bin" });

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValue(result!, "COLUMNS", out var columns)).IsTrue();
        await Assert.That(columns).IsEqualTo("120");
    }

    [Test]
    public async Task BuildUnix_OverlayCannotInjectInternalCwdKey()
    {
        var result = PtyEnvironment.BuildUnix(
            BaseStartInfo() with
            {
                Environment = new Dictionary<string, string?> { ["MINIPTY_CWD"] = "/tmp/hijack" }
            },
            new Dictionary<string, string> { ["MINIPTY_CWD"] = "/tmp/parent", ["PATH"] = "/bin" });

        await Assert.That(result).IsNotNull();
        await Assert.That(ContainsKey(result!, "MINIPTY_CWD")).IsFalse();
        await Assert.That(TryGetValue(result!, "PATH", out var path)).IsTrue();
        await Assert.That(path).IsEqualTo("/bin");
    }

    [Test]
    public async Task BuildWindows_OverlayIsCaseInsensitive()
    {
        const string key = "MINIPTY_TEST_CASE_ENV";
        var result = PtyEnvironment.BuildWindows(
            BaseStartInfo() with { Environment = new Dictionary<string, string?> { [key.ToLowerInvariant()] = "child-value" } },
            new Dictionary<string, string> { [key] = "parent-value", ["PATH"] = "C:\\Windows" });

        await Assert.That(result).IsNotNull();
        await Assert.That(TryGetValueIgnoreCase(result!, key, out var value)).IsTrue();
        await Assert.That(value).IsEqualTo("child-value");
    }

    [Test]
    public async Task BuildWindows_TerminalNameDoesNotCreateTerm()
    {
        var result = PtyEnvironment.BuildWindows(
            BaseStartInfo() with
            {
                TerminalName = "xterm-test",
                Environment = new Dictionary<string, string?>()
            },
            new Dictionary<string, string> { ["PATH"] = "C:\\Windows" });

        await Assert.That(result).IsNotNull();
        await Assert.That(ContainsKey(result!, "TERM")).IsFalse();
    }

    [Test]
    public async Task BuildUnix_RejectsInvalidEnvironmentKeysAndValues()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PtyEnvironment.BuildUnix(
                BaseStartInfo() with { Environment = new Dictionary<string, string?> { ["BAD=KEY"] = "value" } },
                new Dictionary<string, string>());
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PtyEnvironment.BuildUnix(
                BaseStartInfo() with { Environment = new Dictionary<string, string?> { ["BAD"] = "bad\0value" } },
                new Dictionary<string, string>());
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PtyEnvironment.BuildUnix(
                BaseStartInfo() with
                {
                    TerminalName = "bad\0term",
                    Environment = new Dictionary<string, string?>()
                },
                new Dictionary<string, string>());
            return Task.CompletedTask;
        });
    }

    private static bool ContainsKey(KeyValuePair<string, string>[] environment, string key)
    {
        for (var i = 0; i < environment.Length; i++)
        {
            if (environment[i].Key == key)
                return true;
        }

        return false;
    }

    private static bool TryGetValue(KeyValuePair<string, string>[] environment, string key, out string value)
    {
        for (var i = 0; i < environment.Length; i++)
        {
            if (environment[i].Key == key)
            {
                value = environment[i].Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetValueIgnoreCase(KeyValuePair<string, string>[] environment, string key, out string value)
    {
        for (var i = 0; i < environment.Length; i++)
        {
            if (string.Equals(environment[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = environment[i].Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
