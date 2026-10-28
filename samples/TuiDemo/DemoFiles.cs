using TerminalEmulator;

/// <summary>The Files page's navigation state: cursor and first shown row in the visible list, and the folded folders.</summary>
internal readonly record struct FileBrowser(int Cursor, int Top, uint Expanded)
{
    internal static FileBrowser Initial => new(0, 0, DemoFiles.InitiallyExpanded);
}

internal enum FileKind : byte { Folder, CSharp, Markdown, Text, Yaml, Json }

/// <summary>
/// A simulated repository tree for the Files page. It is fixed data rather than the file system, so every terminal and
/// every smoke run shows the same rows. Nodes are kept in preorder with their depth; folding hides a folder's deeper
/// descendants, so the visible list is one pass over the array. Guides, parents, and widths are computed once.
/// </summary>
internal static class DemoFiles
{
    /// <summary>Tree rows shown at once (rows 4–18).</summary>
    internal const int Rows = 15;

    private readonly record struct Node(string Name, int Depth, FileKind Kind, string Size);

    private static readonly Node[] Nodes =
    [
        new("MiniPty", 0, FileKind.Folder, ""),
        new(".github", 1, FileKind.Folder, ""),
        new("workflows", 2, FileKind.Folder, ""),
        new("build.yaml", 3, FileKind.Yaml, "4.2K"),
        new("docs", 2, FileKind.Folder, ""),
        new("spec.md", 3, FileKind.Markdown, "9.8K"),
        new("console.md", 3, FileKind.Markdown, "21K"),
        new("samples", 1, FileKind.Folder, ""),
        new("TerminalEmulator", 2, FileKind.Folder, ""),
        new("ScreenBuffer.cs", 3, FileKind.CSharp, "38K"),
        new("VtParser.cs", 3, FileKind.CSharp, "27K"),
        new("TuiDemo", 2, FileKind.Folder, ""),
        new("DemoFiles.cs", 3, FileKind.CSharp, "9.1K"),
        new("DemoJobs.cs", 3, FileKind.CSharp, "4.6K"),
        new("DemoRenderer.cs", 3, FileKind.CSharp, "24K"),
        new("README.md", 3, FileKind.Markdown, "7.3K"),
        new("src", 1, FileKind.Folder, ""),
        new("MiniPty", 2, FileKind.Folder, ""),
        new("Pty.cs", 3, FileKind.CSharp, "6.4K"),
        new("PtySession.cs", 3, FileKind.CSharp, "18K"),
        new("PtyStartInfo.cs", 3, FileKind.CSharp, "11K"),
        new("MiniPty.Console", 2, FileKind.Folder, ""),
        new("PtyConsoleInput.cs", 3, FileKind.CSharp, "3.9K"),
        new("tests", 1, FileKind.Folder, ""),
        new("MiniPty.Tests", 2, FileKind.Folder, ""),
        new("日本語メモ.txt", 1, FileKind.Text, "412B"),
        new("café.md", 1, FileKind.Markdown, "96B"),
        new("README.md", 1, FileKind.Markdown, "15K"),
        new("global.json", 1, FileKind.Json, "88B"),
    ];

    /// <summary>The root, samples, samples/TuiDemo, src, and src/MiniPty start unfolded: more rows than fit.</summary>
    internal const uint InitiallyExpanded = 1u << 0 | 1u << 7 | 1u << 11 | 1u << 16 | 1u << 17;

    private static readonly int[] Parents = new int[Nodes.Length];
    private static readonly int[] ChildCounts = new int[Nodes.Length];
    private static readonly int[] NameWidths = new int[Nodes.Length];
    private static readonly string[] Prefixes = new string[Nodes.Length];

    private static readonly string[][] Previews =
    [
        [],
        ["namespace MiniPty;", "", "/// <summary>Sample file.</summary>", "internal static class Sample", "{", "    // Preview text only.", "}"],
        ["# MiniPty", "", "A NativeAOT-first PTY library.", "", "- Windows ConPTY", "- Unix PTY"],
        ["これは日本語のメモです。", "全角は 2 セル幅です。", "", "café と日本語も揃います。"],
        ["name: Build", "on:", "  push:", "    branches: [main]", "jobs:", "  build:", "    runs-on: ubuntu-24.04"],
        ["{", "  \"sdk\": {", "    \"version\": \"10.0.100\"", "  }", "}"],
    ];

    static DemoFiles()
    {
        Span<int> ancestors = stackalloc int[8];
        for (var i = 0; i < Nodes.Length; i++)
        {
            var depth = Nodes[i].Depth;
            ancestors[depth] = i;
            Parents[i] = depth == 0 ? -1 : ancestors[depth - 1];
            if (depth > 0)
                ChildCounts[Parents[i]]++;
            NameWidths[i] = DisplayWidth(Nodes[i].Name);
        }

        // A node's guide is one column per ancestor level below the root ("│  " while that ancestor has later
        // siblings) and its own branch ("├─ " or "└─ " for the last child).
        for (var i = 0; i < Nodes.Length; i++)
        {
            var depth = Nodes[i].Depth;
            var guide = new char[Math.Max(0, depth * 3)];
            var node = i;
            for (var level = depth; level >= 1; level--)
            {
                var last = IsLastChild(node);
                var piece = level == depth ? (last ? "└─ " : "├─ ") : (last ? "   " : "│  ");
                piece.CopyTo(guide.AsSpan((level - 1) * 3));
                node = Parents[node];
            }
            Prefixes[i] = new string(guide);
        }
    }

    internal static int Count => Nodes.Length;

    internal static string Name(int node) => Nodes[node].Name;

    internal static FileKind Kind(int node) => Nodes[node].Kind;

    internal static bool IsFolder(int node) => Nodes[node].Kind == FileKind.Folder;

    internal static string Size(int node) => Nodes[node].Size;

    internal static string Prefix(int node) => Prefixes[node];

    internal static int NameWidth(int node) => NameWidths[node];

    internal static int Parent(int node) => Parents[node];

    internal static int Children(int node) => ChildCounts[node];

    internal static ReadOnlySpan<string> Preview(FileKind kind) => Previews[(int)kind];

    internal static bool IsExpanded(uint expanded, int node) => (expanded & 1u << node) != 0;

    /// <summary>Writes the visible nodes, in order, into <paramref name="nodes"/> and returns how many there are.</summary>
    internal static int Visible(uint expanded, Span<int> nodes)
    {
        var count = 0;
        var hiddenBelow = int.MaxValue;
        for (var i = 0; i < Nodes.Length; i++)
        {
            var depth = Nodes[i].Depth;
            if (depth > hiddenBelow)
                continue;
            hiddenBelow = int.MaxValue;
            nodes[count++] = i;
            if (Nodes[i].Kind == FileKind.Folder && !IsExpanded(expanded, i))
                hiddenBelow = depth;
        }
        return count;
    }

    internal static FileBrowser Move(FileBrowser state, int delta)
    {
        Span<int> visible = stackalloc int[Nodes.Length];
        var count = Visible(state.Expanded, visible);
        return Scrolled(state with { Cursor = Math.Clamp(state.Cursor + delta, 0, count - 1) }, count);
    }

    /// <summary>Folds or unfolds the folder under the cursor. The cursor stays on it, above the rows that change.</summary>
    internal static FileBrowser Toggle(FileBrowser state)
    {
        Span<int> visible = stackalloc int[Nodes.Length];
        Visible(state.Expanded, visible);
        var node = visible[state.Cursor];
        if (!IsFolder(node))
            return state;
        var expanded = state.Expanded ^ 1u << node;
        return Scrolled(state with { Expanded = expanded }, Visible(expanded, visible));
    }

    /// <summary>Right: unfolds a folded folder.</summary>
    internal static FileBrowser Expand(FileBrowser state)
    {
        Span<int> visible = stackalloc int[Nodes.Length];
        Visible(state.Expanded, visible);
        var node = visible[state.Cursor];
        return IsFolder(node) && !IsExpanded(state.Expanded, node) ? Toggle(state) : state;
    }

    /// <summary>Left: folds an unfolded folder, or moves to the parent folder.</summary>
    internal static FileBrowser Collapse(FileBrowser state)
    {
        Span<int> visible = stackalloc int[Nodes.Length];
        var count = Visible(state.Expanded, visible);
        var node = visible[state.Cursor];
        if (IsFolder(node) && IsExpanded(state.Expanded, node))
            return Toggle(state);
        var parent = Parents[node];
        for (var i = state.Cursor - 1; i >= 0 && parent >= 0; i--)
        {
            if (visible[i] == parent)
                return Scrolled(state with { Cursor = i }, count);
        }
        return state;
    }

    /// <summary>A click on a shown row selects it; a click on the selected folder folds it.</summary>
    internal static FileBrowser Click(FileBrowser state, int row)
    {
        Span<int> visible = stackalloc int[Nodes.Length];
        var count = Visible(state.Expanded, visible);
        var index = state.Top + row;
        if (row < 0 || row >= Rows || index >= count)
            return state;
        return index == state.Cursor ? Toggle(state) : state with { Cursor = index };
    }

    /// <summary>Display width in terminal cells; wide characters such as Japanese take two.</summary>
    internal static int DisplayWidth(ReadOnlySpan<char> text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += Math.Max(0, CharWidth.Of(rune.Value));
        return width;
    }

    private static FileBrowser Scrolled(FileBrowser state, int count)
    {
        var top = state.Top;
        if (state.Cursor < top)
            top = state.Cursor;
        else if (state.Cursor >= top + Rows)
            top = state.Cursor - Rows + 1;
        return state with { Top = Math.Clamp(top, 0, Math.Max(0, count - Rows)) };
    }

    private static bool IsLastChild(int node)
    {
        var depth = Nodes[node].Depth;
        for (var i = node + 1; i < Nodes.Length && Nodes[i].Depth >= depth; i++)
        {
            if (Nodes[i].Depth == depth)
                return false;
        }
        return true;
    }
}
