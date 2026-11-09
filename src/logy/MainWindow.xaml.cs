using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Logy.Core.Repos;
using Logy.Native;

namespace Logy;

/// <summary>One row of the + picker.</summary>
public sealed record PickerEntry(RepoInfo Repo, bool CanOpen)
{
    public string Name => Repo.Name;

    public string Chip => Repo.Remote.Chip;

    public Visibility ChipVisibility => Chip.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>The floating window: title bar, repo panels as columns, + picker (design doc 6.1, 6.2).</summary>
public partial class MainWindow : Window
{
    private List<PickerEntry> _pickerAll = [];
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DwmHelper.Apply(this);
        RestoreSession();
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private const double PanelMin = 220;
    private const double SplitterWidth = 6;

    private readonly List<RepoPanel> _panels = [];

    private IEnumerable<RepoPanel> Panels => _panels;

    private void RestoreSession()
    {
        var s = App.Settings;
        Width = Math.Max(MinWidth, s.Width);
        Height = Math.Max(MinHeight, s.Height);
        if (s.Left is { } left && s.Top is { } top && IsVisibleOnScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        PinButton.IsChecked = s.AlwaysOnTop;
        Topmost = s.AlwaysOnTop;

        foreach (var path in s.OpenRepos.ToList())
        {
            if (RepoScanner.TryOpen(path) is { } repo)
            {
                AddPanel(repo, save: false);
            }
        }

        SaveOpenRepos();
        UpdateEmptyHint();
    }

    /// <summary>At least 100x50 DIP of the window must lie inside the virtual screen (6.1).</summary>
    private static bool IsVisibleOnScreen(double left, double top, double width, double height)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        var w = Math.Min(left + width, vr) - Math.Max(left, vl);
        var h = Math.Min(top + height, vb) - Math.Max(top, vt);
        return w >= 100 && h >= 50;
    }

    private void AddPanel(RepoInfo repo, bool save = true)
    {
        var panel = new RepoPanel(repo);
        panel.CloseRequested += (_, _) =>
        {
            panel.Shutdown();
            _panels.Remove(panel);
            LayoutPanels();
            SaveOpenRepos();
        };
        _panels.Add(panel);
        LayoutPanels();
        if (save)
        {
            SaveOpenRepos();
            Dispatcher.BeginInvoke(() => PanelScroller.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Rebuilds the star columns: panel, splitter, panel, ... Every panel gets an equal share.</summary>
    private void LayoutPanels()
    {
        PanelHost.Children.Clear();
        PanelHost.ColumnDefinitions.Clear();
        for (var i = 0; i < _panels.Count; i++)
        {
            if (i > 0)
            {
                PanelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterWidth) });
                var splitter = new GridSplitter
                {
                    Width = SplitterWidth,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    ResizeDirection = GridResizeDirection.Columns,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Focusable = false,
                };
                Grid.SetColumn(splitter, PanelHost.ColumnDefinitions.Count - 1);
                PanelHost.Children.Add(splitter);
            }

            PanelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = PanelMin });
            Grid.SetColumn(_panels[i], PanelHost.ColumnDefinitions.Count - 1);
            PanelHost.Children.Add(_panels[i]);
        }

        SizeHost();
        UpdateEmptyHint();
    }

    /// <summary>Host = visible width, or the panels' minimum total when that is wider.</summary>
    private void SizeHost()
    {
        var min = _panels.Count * PanelMin + Math.Max(0, _panels.Count - 1) * SplitterWidth;
        var viewport = PanelScroller.ActualWidth;
        PanelHost.Width = Math.Max(min, viewport > 0 ? viewport : min);
    }

    private void PanelScroller_SizeChanged(object sender, SizeChangedEventArgs e) => SizeHost();

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = _panels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SaveOpenRepos()
    {
        var paths = Panels.Select(p => p.Repo.Path).ToList();
        App.UpdateSettings(s => s.OpenRepos = paths);
    }

    // ---- title bar ----

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        var on = PinButton.IsChecked == true;
        Topmost = on;
        App.UpdateSettings(s => s.AlwaysOnTop = on);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_closingConfirmed)
        {
            var busy = Panels.FirstOrDefault(p => p.RunningCommand is not null);
            if (busy is not null)
            {
                var answer = MessageBox.Show(this, $"{busy.RunningCommand} is running in {busy.Repo.Name}. Quit anyway?",
                    "logy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _closingConfirmed = true;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var paths = Panels.Select(p => p.Repo.Path).ToList();
        var pinned = PinButton.IsChecked == true;
        App.UpdateSettings(s =>
        {
            s.Left = bounds.Left;
            s.Top = bounds.Top;
            s.Width = bounds.Width;
            s.Height = bounds.Height;
            s.AlwaysOnTop = pinned;
            s.OpenRepos = paths;
        });

        foreach (var p in Panels.ToList())
        {
            p.Shutdown();
        }
    }

    // ---- + picker ----

    private void Add_Click(object sender, RoutedEventArgs e) => Picker.IsOpen = !Picker.IsOpen;

    private void Picker_Opened(object? sender, EventArgs e)
    {
        var root = App.Settings.RootFolder;
        var open = Panels.Select(p => p.Repo.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pickerAll = RepoScanner.Scan(root).Select(r => new PickerEntry(r, !open.Contains(r.Path))).ToList();
        PickerFilter.Text = "";
        ApplyFilter();
        PickerEmpty.Text = $"No repos in {root}";
        PickerEmpty.Visibility = _pickerAll.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(PickerFilter), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ApplyFilter()
    {
        var f = PickerFilter.Text.Trim();
        PickerList.ItemsSource = f.Length == 0
            ? _pickerAll
            : _pickerAll.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void PickerFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void PickerFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Picker.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && PickerList.Items.Count > 0)
        {
            PickerList.SelectedIndex = 0;
            (PickerList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var first = PickerList.Items.OfType<PickerEntry>().FirstOrDefault(p => p.CanOpen);
            if (first is not null)
            {
                Open(first);
            }

            e.Handled = true;
        }
    }

    private void PickerList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } src
            && ItemsControl.ContainerFromElement(PickerList, src) is ListBoxItem { DataContext: PickerEntry entry })
        {
            Open(entry);
        }
    }

    private void PickerList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PickerList.SelectedItem is PickerEntry entry)
        {
            Open(entry);
        }
        else if (e.Key == Key.Escape)
        {
            Picker.IsOpen = false;
        }
    }

    private void Open(PickerEntry entry)
    {
        if (!entry.CanOpen)
        {
            return;
        }

        Picker.IsOpen = false;
        AddPanel(entry.Repo);
    }
}
