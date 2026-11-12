using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Logy;

/// <summary>What the user filled in; the panel pushes the branch if needed, then creates the pull request.</summary>
public sealed record NewPull(string Source, string Target, string Title, string Description);

/// <summary>"New pull request" form: source branch, target branch, title, description.</summary>
public partial class CreatePullWindow : Window
{
    private readonly Func<string, string> _titleFor;
    private string _autoTitle = "";

    private CreatePullWindow(IReadOnlyList<string> sources, IReadOnlyList<string> targets, string? source, string? target,
        string remote, Func<string, string> titleFor)
    {
        InitializeComponent();
        Native.DwmHelper.Apply(this);
        _titleFor = titleFor;
        TargetLabel.Text = $"INTO (on {remote})";
        SourceList.ItemsSource = sources;
        TargetList.ItemsSource = targets;
        TargetList.SelectedItem = target;
        SourceList.SelectedItem = source;
        Loaded += (_, _) =>
        {
            SourceList.ScrollIntoView(SourceList.SelectedItem);
            TargetList.ScrollIntoView(TargetList.SelectedItem);
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    public NewPull? Result { get; private set; }

    public static NewPull? Ask(Window owner, IReadOnlyList<string> sources, IReadOnlyList<string> targets,
        string? source, string? target, string remote, Func<string, string> titleFor)
    {
        var w = new CreatePullWindow(sources, targets, source, target, remote, titleFor) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Suggest the branch tip's subject as title, unless the user already typed their own.
        if (SourceList.SelectedItem is string b && (TitleBox.Text.Length == 0 || TitleBox.Text == _autoTitle))
        {
            _autoTitle = _titleFor(b);
            TitleBox.Text = _autoTitle;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceList.SelectedItem as string;
        var target = TargetList.SelectedItem as string;
        var title = TitleBox.Text.Trim();
        ValidationText.Text = source is null ? "pick a source branch"
            : target is null ? "pick a target branch"
            : source == target ? "source and target are the same"
            : title.Length == 0 ? "enter a title"
            : "";
        if (ValidationText.Text.Length > 0)
        {
            return;
        }

        Result = new NewPull(source!, target!, title, DescriptionBox.Text.Trim());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
