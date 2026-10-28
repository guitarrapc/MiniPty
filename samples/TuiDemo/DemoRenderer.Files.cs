/// <summary>
/// The Files page: a tree (columns 1–40) and a Details panel with a preview (42–72). Moving the cursor rewrites the
/// old and new rows and the details; scrolling or folding rewrites the tree rows. Scrolling does not use a scroll
/// region, because DECSTBM scrolls whole rows and would move the Details panel too.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const int NameCells = 31;
    private const int DetailCells = 29;

    private readonly int[] _visible = new int[DemoFiles.Count];

    private void DrawFiles(DemoView view)
    {
        var count = DemoFiles.Visible(view.Files.Expanded, _visible);
        MoveTo(3, 1).Append("\u001b[0m┌ Files ").Append('─', 31).Append("┐ ┌ Details ").Append('─', 20).Append('┐');
        for (var k = 0; k < DemoFiles.Rows; k++)
            DrawFileRow(k, view, count);
        MoveTo(19, 1).Append("\u001b[0m└").Append('─', 38).Append("┘ └").Append('─', DetailCells).Append('┘');
        DrawFileDetails(view, count);
    }

    private void DrawFilesChanges(DemoView view)
    {
        var before = _previous.Files;
        var now = view.Files;
        if (before == now)
            return;

        var count = DemoFiles.Visible(now.Expanded, _visible);
        if (before.Expanded != now.Expanded || before.Top != now.Top)
        {
            for (var k = 0; k < DemoFiles.Rows; k++)
                DrawFileRow(k, view, count);
        }
        else
        {
            DrawFileRow(before.Cursor - now.Top, view, count);
            DrawFileRow(now.Cursor - now.Top, view, count);
        }
        DrawFileDetails(view, count);
    }

    /// <summary>
    /// Columns 1–41 of tree row <paramref name="k"/>: guide, folder marker, name padded by display width, size.
    /// Folders use ASCII + and - markers; ▸ and ▾ are East Asian Ambiguous and are drawn wide by some terminals.
    /// </summary>
    private void DrawFileRow(int k, DemoView view, int count)
    {
        var index = view.Files.Top + k;
        MoveTo(DemoLayout.FirstFileRow + k, 1).Append("\u001b[0m│");
        if (index >= count)
        {
            _frame.Append(' ', 38).Append("│ ");
            return;
        }

        var node = _visible[index];
        var selected = index == view.Files.Cursor;
        var folder = DemoFiles.IsFolder(node);
        _frame.Append(selected ? "\u001b[30;46m " : " \u001b[90m").Append(DemoFiles.Prefix(node));
        var width = 1 + DemoFiles.Prefix(node).Length;
        if (folder)
        {
            _frame.Append(DemoFiles.IsExpanded(view.Files.Expanded, node) ? "- " : "+ ");
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
        _frame.Append(' ', NameCells - width).Append(' ', 6 - size.Length).Append(size).Append(" \u001b[0m│ ");
    }

    private void DrawFileDetails(DemoView view, int count)
    {
        var node = _visible[Math.Clamp(view.Files.Cursor, 0, count - 1)];
        var kind = DemoFiles.Kind(node);

        DetailLine(4).Append(" Name: ");
        AppendName(node, DetailCells - 7);
        _frame.Append('│');

        DetailLine(5).Append(" Type: ");
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
        DetailLine(6).Append(" Size: ").Append(size.Length == 0 ? "-" : size)
            .Append(' ', DetailCells - 7 - Math.Max(1, size.Length)).Append('│');

        DetailLine(7).Append(" Path: ");
        AppendPath(node, DetailCells - 7);
        _frame.Append('│');
        DetailLine(8).Append(' ', DetailCells).Append('│');
        MoveTo(9, 42).Append("├ Preview ").Append('─', 20).Append('┤');

        // A folder previews its children; a file shows sample lines for its type.
        var line = 0;
        if (kind == FileKind.Folder)
        {
            for (var child = node + 1; child < DemoFiles.Count && line < 9 && DemoFiles.Parent(child) >= node; child++)
            {
                if (DemoFiles.Parent(child) != node)
                    continue;
                DetailLine(10 + line++).Append(' ').Append(KindColor(DemoFiles.Kind(child)));
                AppendName(child, DetailCells - 1);
                _frame.Append("\u001b[39m│");
            }
        }
        else
        {
            foreach (var text in DemoFiles.Preview(kind))
            {
                if (line == 9)
                    break;
                DetailLine(10 + line++).Append(' ');
                AppendFitted(text, DetailCells - 1);
                _frame.Append('│');
            }
        }
        while (line < 9)
            DetailLine(10 + line++).Append(' ', DetailCells).Append('│');
    }

    private System.Text.StringBuilder DetailLine(int row) => MoveTo(row, 42).Append("\u001b[0m│");

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
