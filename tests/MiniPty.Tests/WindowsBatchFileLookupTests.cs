using System.ComponentModel;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Where a batch file is found (<c>WindowsBatchFile.ResolveScript</c>): the file <c>CreateProcess</c> would find for a
/// program of that name. Every location the lookup reads is a directory of a fresh tree, so each rule is tested apart
/// from the real system directories and the host's environment.
/// </summary>
public sealed class WindowsBatchFileLookupTests
{
    [Test]
    [Arguments("")]
    [Arguments("sub\\")]
    public async Task NameIsSearchedInCreateProcessOrder(string directoryPart)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The file is in every location from the i-th on, so the i-th is found only if it comes before all later ones.
        using var tree = new LookupTree();
        string[] order = [tree.Host, tree.Current, tree.System, tree.System16, tree.Windows, tree.PathEntry];
        for (var i = 0; i < order.Length; i++)
        {
            var name = $"{directoryPart}order{i}.cmd";
            for (var j = i; j < order.Length; j++)
                tree.Put(order[j], name);

            await Assert.That(WindowsBatchFile.ResolveScript(name, tree.Locations())).IsEqualTo(Path.Combine(order[i], name));
        }
    }

    [Test]
    public async Task CurrentDirectoryIsSkippedWhenNotSearched()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.Current, "x.cmd");
        tree.Put(tree.PathEntry, "x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(searchCurrentDirectory: false)))
            .IsEqualTo(Path.Combine(tree.PathEntry, "x.cmd"));
    }

    [Test]
    [Arguments(".\\x.cmd")]
    [Arguments("./x.cmd")]
    [Arguments("..\\work\\x.cmd")]
    [Arguments("../work/x.cmd")]
    public async Task DotRelativeNameIsFoundInTheCurrentDirectory(string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The host directory is searched first for every other name, and it holds a copy at the same relative path.
        using var tree = new LookupTree();
        tree.Put(tree.Host, name);
        tree.Put(tree.Current, name);

        await Assert.That(WindowsBatchFile.ResolveScript(name, tree.Locations()))
            .IsEqualTo(Path.GetFullPath(name, tree.Current));
    }

    [Test]
    [Arguments(".\\x.cmd")]
    [Arguments("..\\work\\x.cmd")]
    public async Task DotRelativeNameIsNotSearchedFor(string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.Host, name);
        tree.Put(tree.PathEntry, "x.cmd");

        var error = Assert.Throws<Win32Exception>(() => WindowsBatchFile.ResolveScript(name, tree.Locations()));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    [Arguments("")]
    [Arguments(".")]
    public async Task EmptyOrDotPathEntryIsTheCurrentDirectory(string entry)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The current directory is not searched on its own here, so only the entry can find the file.
        using var tree = new LookupTree();
        tree.Put(tree.Current, "x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(searchCurrentDirectory: false, path: entry + ";" + tree.PathEntry)))
            .IsEqualTo(Path.Combine(tree.Current, "x.cmd"));
    }

    [Test]
    public async Task EmptyEntryAfterTheLastSemicolonIsNotTheCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.Current, "x.cmd");

        var error = Assert.Throws<Win32Exception>(() =>
            WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(searchCurrentDirectory: false, path: tree.PathEntry + ";")));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyPathEntryBetweenOthersIsTheCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The current directory is not searched on its own here, and the entries around the empty one hold nothing.
        using var tree = new LookupTree();
        tree.Put(tree.Current, "x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(searchCurrentDirectory: false, path: tree.PathEntry + ";;" + tree.System)))
            .IsEqualTo(Path.Combine(tree.Current, "x.cmd"));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task UnknownHostDirectoryIsSkipped(string? hostDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // An empty host directory joined with the name would point into the current directory, which is not searched
        // here.
        using var tree = new LookupTree();
        tree.Put(tree.Current, "x.cmd");
        tree.Put(tree.PathEntry, "x.cmd");
        var locations = tree.Locations(searchCurrentDirectory: false) with { ProcessDirectory = hostDirectory };

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", locations)).IsEqualTo(Path.Combine(tree.PathEntry, "x.cmd"));
    }

    [Test]
    public async Task UnsetPathStillSearchesTheFixedLocations()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.Windows, "x.cmd");
        var locations = tree.Locations() with { Path = null };

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", locations)).IsEqualTo(Path.Combine(tree.Windows, "x.cmd"));
        var error = Assert.Throws<Win32Exception>(() => WindowsBatchFile.ResolveScript("y.cmd", locations));
        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    [Arguments("sub\\x.cmd\\.")]
    [Arguments("sub\\x.cmd\\other\\..")]
    public async Task NameWithDotSegmentsIsFoundAsTheFileWindowsOpens(string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, "sub\\x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript(name, tree.Locations())).IsEqualTo(Path.Combine(tree.PathEntry, "sub", "x.cmd"));
    }

    [Test]
    public async Task RelativePathEntryIsRelativeToTheCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(Path.Combine(tree.Current, "rel"), "x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(searchCurrentDirectory: false, path: "rel")))
            .IsEqualTo(Path.Combine(tree.Current, "rel", "x.cmd"));
    }

    [Test]
    [Arguments("\"{0}\"")]
    [Arguments(" {0} ")]
    [Arguments("{0} ")]
    public async Task PathEntryIsNeitherUnquotedNorTrimmed(string shape)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, "x.cmd");

        var error = Assert.Throws<Win32Exception>(() =>
            WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(path: string.Format(shape, tree.PathEntry))));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task PathEntryWithATrailingBackslashIsSearched()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        tree.Put(tree.PathEntry, "x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript("x.cmd", tree.Locations(path: tree.PathEntry + "\\")))
            .IsEqualTo(Path.Combine(tree.PathEntry, "x.cmd"));
    }

    [Test]
    [Arguments("x.cmd")]
    [Arguments(".\\x.cmd")]
    [Arguments("rooted")]
    public async Task DirectoryFoundFirstFailsTheLaunch(string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A file of the name later in the search must not run in its place.
        using var tree = new LookupTree();
        Directory.CreateDirectory(Path.Combine(tree.Current, "x.cmd"));
        tree.Put(tree.PathEntry, "x.cmd");
        var fileName = name == "rooted" ? Path.Combine(tree.Current, "x.cmd") : name;

        var error = Assert.Throws<Win32Exception>(() => WindowsBatchFile.ResolveScript(fileName, tree.Locations()));

        await Assert.That(error.NativeErrorCode).IsEqualTo(5);
    }

    [Test]
    public async Task RootedNameIsTakenAsItIs()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The file is in no searched directory, and the host directory, searched first, holds another of its name.
        using var tree = new LookupTree();
        var elsewhere = Path.Combine(tree.Current, "elsewhere");
        tree.Put(elsewhere, "x.cmd");
        tree.Put(tree.Host, "x.cmd");
        var path = Path.Combine(elsewhere, "x.cmd");

        await Assert.That(WindowsBatchFile.ResolveScript(path, tree.Locations())).IsEqualTo(path);
    }

    [Test]
    [Arguments("x.cmd")]
    [Arguments("sub\\x.cmd")]
    [Arguments("rooted")]
    [Arguments("rooted-missing-directory")]
    public async Task MissingScriptFailsAsFromCreateProcess(string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LookupTree();
        var fileName = name switch
        {
            "rooted" => Path.Combine(tree.Current, "x.cmd"),
            "rooted-missing-directory" => Path.Combine(tree.Current, "none", "x.cmd"),
            _ => name,
        };

        var error = Assert.Throws<Win32Exception>(() => WindowsBatchFile.ResolveScript(fileName, tree.Locations()));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    /// <summary>
    /// One directory for each place the lookup reads, under a fresh temporary root. The host and current directories
    /// sit under different parents, so a <c>..\</c> name means a different file from each.
    /// </summary>
    private sealed class LookupTree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"minipty-lookup-{Guid.NewGuid():N}");

        public LookupTree()
        {
            foreach (var directory in new[] { Host, Current, System, System16, PathEntry })
                Directory.CreateDirectory(directory);
        }

        public string Host => Path.Combine(_root, "host", "bin");
        public string Current => Path.Combine(_root, "user", "work");
        public string System => Path.Combine(_root, "sys");
        public string Windows => Path.Combine(_root, "win");
        public string System16 => Path.Combine(Windows, "System");
        public string PathEntry => Path.Combine(_root, "path");

        public void Put(string directory, string name)
        {
            var path = Path.GetFullPath(name, directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "@echo off\r\n");
        }

        public WindowsBatchFile.SearchLocations Locations(bool searchCurrentDirectory = true, string? path = null) =>
            new(Host, Current, searchCurrentDirectory, System, Windows, path ?? PathEntry);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
