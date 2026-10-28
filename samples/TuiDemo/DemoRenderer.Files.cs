using TerminalEmulator;

/// <summary>
/// The Files page: a tree with a filter box (columns 1 to <see cref="DemoGeometry.TreeWidth"/>) and a 31-column Details
/// panel with a preview at the right edge. Moving the cursor rewrites the old and new rows and the details; scrolling,
/// folding, or filtering rewrites the tree rows. Scrolling does not use a scroll region, because DECSTBM scrolls whole
/// rows and would move the Details panel too.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const int DetailCells = 29;
    private const string FilterHint = "press / to filter";

    private readonly int[] _visible = new int[DemoFiles.Count];

    private void DrawFiles(DemoView view, DemoGeometry g)
    {
        var count = DemoFiles.Visible(view.Files, _visible);
        MoveTo(3, 1).Append("\u001b[0m┌ Files ").Append('─', g.TreeWidth - 9).Append("┐ ┌ Details ").Append('─', 20).Append('┐');
        DrawFilter(view, g);
        for (var k = 0; k < g.FileRows; k++)
            DrawFileRow(k, view, g, count);
        MoveTo(g.PageBottom, 1).Append("\u001b[0m└").Append('─', g.TreeWidth - 2).Append("┘ └").Append('─', DetailCells).Append('┘');
        DrawFileDetails(view, g, count);
    }

    private void DrawFilesChanges(DemoView view, DemoGeometry g)
    {
        var before = _previous.Files;
        var now = view.Files;
        if (before == now)
            return;

        if (before.Filter != now.Filter || before.Editing != now.Editing)
            DrawFilter(view, g);
        if (before.Editing != now.Editing)
        {
            DrawHelp(view, g);
            DrawBottomBar(view, g);
        }
        if (before with { Editing = now.Editing } == now)
            return;

        var count = DemoFiles.Visible(now, _visible);
        if (before.Expanded != now.Expanded || before.Top != now.Top || before.Filter != now.Filter)
        {
            for (var k = 0; k < g.FileRows; k++)
                DrawFileRow(k, view, g, count);
        }
        else
        {
            DrawFileRow(before.Cursor - now.Top, view, g, count);
            DrawFileRow(now.Cursor - now.Top, view, g, count);
        }
        DrawFileDetails(view, g, count);
    }

    /// <summary>
    /// Row 4: "│ / " and the box. While typing it has its own background and shows the end of the text, leaving a cell
    /// for the cursor; with a filter it shows the text; otherwise a hint.
    /// </summary>
    private void DrawFilter(DemoView view, DemoGeometry g)
    {
        var cells = g.FilterCells;
        MoveTo(DemoLayout.FilterRow, 1).Append("\u001b[0m│ \u001b[90m/\u001b[0m ");
        var filter = view.Files.Filter ?? string.Empty;
        if (view.Files.Editing)
        {
            _frame.Append("\u001b[97;48;5;238m");
            AppendTail(filter, cells - 1);
            _frame.Append(' ');
        }
        else if (filter.Length > 0)
        {
            _frame.Append("\u001b[33m");
            AppendFitted(filter, cells);
        }
        else
        {
            _frame.Append("\u001b[90m");
            AppendFitted(FilterHint, cells);
        }
        _frame.Append("\u001b[0m │ ");
    }

    private int FilterCursorColumn(DemoView view, DemoGeometry g) =>
        DemoLayout.FilterLeft + Math.Min(DemoFiles.DisplayWidth(view.Files.Filter ?? string.Empty), g.FilterCells - 1);

    /// <summary>The end of <paramref name="text"/> that fits in <paramref name="cells"/>, padded on the right.</summary>
    private void AppendTail(string text, int cells)
    {
        var start = 0;
        while (DemoFiles.DisplayWidth(text.AsSpan(start)) > cells)
            start += char.IsHighSurrogate(text[start]) ? 2 : 1;
        AppendFitted(text.AsSpan(start), cells);
    }

    /// <summary>
    /// Columns 1 to the tree width plus the gap after it, for tree row <paramref name="k"/>: guide, folder marker, name
    /// padded by display width, size. Folders use ASCII + and - markers; ▸ and ▾ are East Asian Ambiguous and are
    /// drawn wide by some terminals. A filter shows every shown folder open.
    /// </summary>
    private void DrawFileRow(int k, DemoView view, DemoGeometry g, int count)
    {
        var index = view.Files.Top + k;
        var nameCells = g.TreeWidth - 9;
        MoveTo(DemoLayout.FirstFileRow + k, 1).Append("\u001b[0m│");
        if (index >= count)
        {
            _frame.Append(' ', g.TreeWidth - 2).Append("│ ");
            return;
        }

        var node = _visible[index];
        var selected = index == view.Files.Cursor;
        var folder = DemoFiles.IsFolder(node);
        _frame.Append(selected ? "\u001b[30;46m " : " \u001b[90m").Append(DemoFiles.Prefix(node));
        var width = 1 + DemoFiles.Prefix(node).Length;
        if (folder)
        {
            _frame.Append(view.Files.Filtering || DemoFiles.IsExpanded(view.Files.Expanded, node) ? "- " : "+ ");
            width += 2;
        }
        if (!selected)
            _frame.Append(KindColor(DemoFiles.Kind(node)));
        _frame.Append(DemoFiles.Name(node));
        width += DemoFiles.NameWidth(node);
        if (folder)
        {
            _frame.Append('/');
            width++;
        }
        if (!selected)
            _frame.Append("\u001b[39m");
        var size = DemoFiles.Size(node);
        _frame.Append(' ', Math.Max(0, nameCells - width)).Append(' ', 6 - size.Length).Append(size).Append(" \u001b[0m│ ");
    }

    private void DrawFileDetails(DemoView view, DemoGeometry g, int count)
    {
        var line = 0;
        if (count == 0)
        {
            DetailLine(4, g).Append(' ');
            AppendFitted("No item matches the filter.", DetailCells - 1);
            _frame.Append('│');
            for (var row = 5; row <= 8; row++)
                DetailLine(row, g).Append(' ', DetailCells).Append('│');
            MoveTo(9, g.DetailsColumn).Append("├ Preview ").Append('─', 20).Append('┤');
        }
        else
        {
            var node = _visible[view.Files.Cursor];
            var kind = DemoFiles.Kind(node);

            DetailLine(4, g).Append(" Name: ");
            AppendName(node, DetailCells - 7);
            _frame.Append('│');

            DetailLine(5, g).Append(" Type: ");
            if (kind == FileKind.Folder)
            {
                var children = DemoFiles.Children(node);
                _frame.Append("folder, ").Append(children).Append(children == 1 ? " item " : " items")
                    .Append(' ', DetailCells - 21 - Digits(children));
            }
            else
            {
                var type = KindText(kind);
                _frame.Append(type).Append(' ', DetailCells - 7 - type.Length);
            }
            _frame.Append('│');

            var size = DemoFiles.Size(node);
            DetailLine(6, g).Append(" Size: ").Append(size.Length == 0 ? "-" : size)
                .Append(' ', DetailCells - 7 - Math.Max(1, size.Length)).Append('│');

            DetailLine(7, g).Append(" Path: ");
            AppendPath(node, DetailCells - 7);
            _frame.Append('│');
            DetailLine(8, g).Append(' ', DetailCells).Append('│');
            MoveTo(9, g.DetailsColumn).Append("├ Preview ").Append('─', 20).Append('┤');

            // A folder previews its children; a file shows sample lines for its type.
            if (kind == FileKind.Folder)
            {
                for (var child = node + 1; child < DemoFiles.Count && line < g.PreviewLines && DemoFiles.Parent(child) >= node; child++)
                {
                    if (DemoFiles.Parent(child) != node)
                        continue;
                    DetailLine(10 + line++, g).Append(' ').Append(KindColor(DemoFiles.Kind(child)));
                    AppendName(child, DetailCells - 1);
                    _frame.Append("\u001b[39m│");
                }
            }
            else
            {
                foreach (var text in DemoFiles.Preview(kind))
                {
                    if (line == g.PreviewLines)
                        break;
                    DetailLine(10 + line++, g).Append(' ');
                    AppendFitted(text, DetailCells - 1);
                    _frame.Append('│');
                }
            }
        }
        while (line < g.PreviewLines)
            DetailLine(10 + line++, g).Append(' ', DetailCells).Append('│');
    }

    private System.Text.StringBuilder DetailLine(int row, DemoGeometry g) => MoveTo(row, g.DetailsColumn).Append("\u001b[0m│");

    /// <summary>A name with a folder's trailing slash, in exactly <paramref name="cells"/> cells.</summary>
    private void AppendName(int node, int cells)
    {
        var fitted = Math.Min(DemoFiles.NameWidth(node), cells - 1);
        AppendFitted(DemoFiles.Name(node), fitted);
        _frame.Append(DemoFiles.IsFolder(node) ? '/' : ' ').Append(' ', cells - 1 - fitted);
    }

    /// <summary>The parent folders from the root, cut from the left with "..." when longer than <paramref name="cells"/>.</summary>
    private void AppendPath(int node, int cells)
    {
        Span<int> chain = stackalloc int[8];
        var depth = 0;
        for (var parent = DemoFiles.Parent(node); parent >= 0; parent = DemoFiles.Parent(parent))
            chain[depth++] = parent;
        if (depth == 0)
        {
            AppendFitted("(repository root)", cells);
            return;
        }

        // Folder names are ASCII, so length equals display width here.
        Span<char> path = stackalloc char[128];
        var length = 0;
        for (var i = depth - 1; i >= 0; i--)
        {
            var name = DemoFiles.Name(chain[i]);
            name.CopyTo(path[length..]);
            length += name.Length;
            path[length++] = '/';
        }
        if (length <= cells)
            AppendFitted(path[..length], cells);
        else
            _frame.Append("...").Append(path[(length - cells + 3)..length]);
    }

    private static string KindColor(FileKind kind) => kind switch
    {
        FileKind.Folder => "\u001b[94m",
        FileKind.CSharp => "\u001b[32m",
        FileKind.Markdown => "\u001b[33m",
        FileKind.Yaml => "\u001b[35m",
        FileKind.Json => "\u001b[36m",
        _ => "\u001b[37m",
    };

    private static string KindText(FileKind kind) => kind switch
    {
        FileKind.CSharp => "C# source",
        FileKind.Markdown => "Markdown",
        FileKind.Text => "Text",
        FileKind.Yaml => "YAML",
        _ => "JSON",
    };
}
