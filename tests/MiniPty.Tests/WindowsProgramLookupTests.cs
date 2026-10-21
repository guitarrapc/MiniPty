using System.ComponentModel;
using MiniPty.Internal;
using static MiniPty.Tests.WindowsBatchFileLookupTests;

namespace MiniPty.Tests;

/// <summary>
/// Where a program is found when MiniPty searches for it (<c>WindowsProgramLookup.ResolveProgram</c>): the file
/// <c>CreateProcess</c> would start, with <c>.exe</c> appended by its rule. Every location is a directory of a fresh
/// tree, so each rule is tested apart from the real system directories and the host's environment. The script rules
/// shared with batch files are covered by <see cref="WindowsBatchFileLookupTests"/>.
/// </summary>
public sealed class WindowsProgramLookupTests
{
    [Test]
    public async Task BareNameFindsOnlyTheExeForm()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Measured: "tool" ran tool.exe when both files sat in the same directory, and found nothing with only "tool".
        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, "tool");
        var error = Assert.Throws<Win32Exception>(() => WindowsProgramLookup.ResolveProgram("tool", tree.Locations()));
        await Assert.That(error.NativeErrorCode).IsEqualTo(2);

        tree.Put(tree.PathEntry, "tool.exe");
        await Assert.That(WindowsProgramLookup.ResolveProgram("tool", tree.Locations())).IsEqualTo(Path.Combine(tree.PathEntry, "tool.exe"));
    }

    [Test]
    [Arguments("tool.foo", "tool.foo")]
    [Arguments(".tool", ".tool")]
    [Arguments("tool.", "tool")]
    public async Task NameWithADotIsTakenAsWritten(string name, string file)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A dot in the last component means an extension to CreateProcess, so nothing is appended; Windows opens
        // "tool." as "tool". An .exe of the same stem is there to show it is not the one found.
        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, file);
        tree.Put(tree.PathEntry, file + ".exe");

        await Assert.That(WindowsProgramLookup.ResolveProgram(name, tree.Locations())).IsEqualTo(Path.Combine(tree.PathEntry, file));
    }

    [Test]
    [Arguments("sub\\tool", "sub\\tool.exe")]
    [Arguments("sub/tool", "sub\\tool.exe")]
    [Arguments("sub.d\\tool", "sub.d\\tool.exe")]
    public async Task NameWithADirectoryPartStillGetsExe(string name, string file)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Measured, against the documentation: "sub\tool" ran sub\tool.exe and did not find an extensionless
        // sub\tool, and a dot in the directory part does not count as an extension.
        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, file);
        tree.Put(tree.PathEntry, file[..^4]);

        await Assert.That(WindowsProgramLookup.ResolveProgram(name, tree.Locations())).IsEqualTo(Path.Combine(tree.PathEntry, file));
    }

    [Test]
    public async Task ExtensionlessFileWithADirectoryPartIsNotFound()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, "sub\\tool");

        var error = Assert.Throws<Win32Exception>(() => WindowsProgramLookup.ResolveProgram("sub\\tool", tree.Locations()));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task NameIsSearchedInCreateProcessOrder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The file is in every location from the i-th on, so the i-th is found only if it comes before all later ones.
        using var tree = new LookupTree();
        string[] order = [tree.Host, tree.Current, tree.System, tree.System16, tree.Windows, tree.PathEntry];
        for (var i = 0; i < order.Length; i++)
        {
            for (var j = i; j < order.Length; j++)
                tree.Put(order[j], $"order{i}.exe");

            await Assert.That(WindowsProgramLookup.ResolveProgram($"order{i}", tree.Locations())).IsEqualTo(Path.Combine(order[i], $"order{i}.exe"));
        }
    }

    [Test]
    public async Task OnlyTheGivenPathIsSearched()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The PATH in the locations is the child's; a file on any other PATH is not found, and with no PATH only
        // the fixed locations are searched.
        using var tree = new LookupTree();
        var other = Path.Combine(tree.Current, "other");
        tree.Put(tree.PathEntry, "tool.exe");

        await Assert.That(WindowsProgramLookup.ResolveProgram("tool", tree.Locations(searchCurrentDirectory: false))).IsEqualTo(Path.Combine(tree.PathEntry, "tool.exe"));
        var elsewhere = Assert.Throws<Win32Exception>(() => WindowsProgramLookup.ResolveProgram("tool", tree.Locations(searchCurrentDirectory: false, path: other)));
        await Assert.That(elsewhere.NativeErrorCode).IsEqualTo(2);
        var none = Assert.Throws<Win32Exception>(() => WindowsProgramLookup.ResolveProgram("tool", tree.Locations(searchCurrentDirectory: false) with { Path = null }));
        await Assert.That(none.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task DirectoryNamedLikeTheProgramFailsTheLaunch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Measured: a tool.exe directory first in PATH failed with ERROR_ACCESS_DENIED although a file came later.
        using var tree = new LookupTree();
        Directory.CreateDirectory(Path.Combine(tree.Current, "tool.exe"));
        tree.Put(tree.PathEntry, "tool.exe");

        var error = Assert.Throws<Win32Exception>(() => WindowsProgramLookup.ResolveProgram("tool", tree.Locations()));

        await Assert.That(error.NativeErrorCode).IsEqualTo(5);
    }

    [Test]
    [Arguments("tool", true)]
    [Arguments("sub\\tool", true)]
    [Arguments("sub/tool", true)]
    [Arguments("sub.d\\tool", true)]
    [Arguments("tool.exe", false)]
    [Arguments("tool.", false)]
    [Arguments(".tool", false)]
    [Arguments("sub\\tool.cmd", false)]
    public async Task ExeIsAppendedWhenTheLastComponentHasNoDot(string fileName, bool expected)
    {
        await Assert.That(WindowsProgramLookup.NeedsExecutableExtension(fileName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("tool", true)]
    [Arguments("sub\\tool", true)]
    [Arguments("sub/tool", true)]
    [Arguments("C:\\x\\tool", false)]
    [Arguments("C:tool", false)]
    [Arguments("\\tool", false)]
    [Arguments(".\\tool", false)]
    [Arguments("./tool", false)]
    [Arguments("..\\tool", false)]
    [Arguments("../tool", false)]
    public async Task OnlyNamesWithoutARootOrADotPrefixAreSearchedFor(string fileName, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return;

        await Assert.That(WindowsProgramLookup.IsSearched(fileName)).IsEqualTo(expected);
    }
}
