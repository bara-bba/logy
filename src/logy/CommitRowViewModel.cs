using System.ComponentModel;
using System.Windows.Media;
using Logy.Core.Graph;
using Logy.Core.Theming;

namespace Logy;

public sealed record Pill(string Text, Brush Background, Brush Foreground);

/// <summary>One item of a panel's commit list: a commit row, or the "newest 2000" footer.</summary>
public sealed class CommitRowViewModel : INotifyPropertyChanged
{
    private static readonly Brush TagBackground = App.Brush("#4A4A4A");
    private static readonly Brush White = App.Brush("#FFFFFF");

    private CommitRowViewModel(GraphRow? row, string footer)
    {
        Row = row;
        FooterText = footer;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public GraphRow? Row { get; }

    public bool IsFooter => Row is null;

    public string FooterText { get; }

    public string Hash => Row?.Commit.Hash ?? "";

    public string ShortHash => Row?.Commit.ShortHash ?? "";

    public string Subject => Row?.Commit.Subject ?? FooterText;

    public string Date { get; private set; } = "";

    public string Tooltip { get; private set; } = "";

    public IReadOnlyList<Pill> Pills { get; private set; } = [];

    public static CommitRowViewModel Footer(int count) => new(null, $"showing the newest {count} commits");

    public static CommitRowViewModel For(GraphRow row, GraphStyle style, DateTimeOffset now)
    {
        var vm = new CommitRowViewModel(row, "");
        var c = row.Commit;
        vm.Date = RelativeTime.Format(c.UnixTime, now);
        var local = DateTimeOffset.FromUnixTimeSeconds(c.UnixTime).ToLocalTime();
        vm.Tooltip = $"{c.Author}\n{local:yyyy-MM-dd HH:mm}\n{c.Hash}";
        vm.BuildPills(style);
        return vm;
    }

    /// <summary>Rebuilds the pill colours after a palette change.</summary>
    public void Recolour(GraphStyle style)
    {
        BuildPills(style);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pills)));
    }

    private void BuildPills(GraphStyle style)
    {
        if (Row is null)
        {
            return;
        }

        var refs = Row.Commit.Refs;
        var pills = new List<Pill>();
        foreach (var r in refs.Where(r => r.Kind == RefKind.Local).Concat(refs.Where(r => r.Kind == RefKind.Remote)))
        {
            var index = style.Index(LogParser.BranchName(r));
            pills.Add(new Pill(r.Name, style.Brushes[index], App.Brush(Palettes.Ink(style.Hex[index]))));
        }

        foreach (var r in refs.Where(r => r.Kind == RefKind.Tag))
        {
            pills.Add(new Pill(r.Name, TagBackground, White));
        }

        Pills = pills;
    }
}
