using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Logy.Core.Git;
using Logy.Core.Graph;
using Logy.Core.Pulls;
using Logy.Core.Repos;

namespace Logy;

/// <summary>One repo column: header, error bar, commit graph, details pane (design doc 6.3).</summary>
public partial class RepoPanel : UserControl
{
    public static readonly DependencyProperty GraphWidthProperty = DependencyProperty.Register(
        nameof(GraphWidth), typeof(double), typeof(RepoPanel), new PropertyMetadata(21.0));

    public static readonly DependencyProperty GraphStyleProperty = DependencyProperty.Register(
        nameof(GraphStyle), typeof(GraphStyle), typeof(RepoPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty GraphClippedProperty = DependencyProperty.Register(
        nameof(GraphClipped), typeof(bool), typeof(RepoPanel), new PropertyMetadata(false));

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _debounce;

    private int _jobs;
    private bool _refreshPending;
    private bool _shutdown;

    private string? _fingerprint;
    private RepoStatus _status = new();
    private List<RefLine> _refLines = [];
    private string _mainBranch = "HEAD";
    private List<CommitRowViewModel> _rows = [];
    private int _maxSlots = 1;

    private Button? _failedButton;
    private Button? _spinningButton;
    private object? _spinningContent;
    private string _errorShort = "";
    private string _errorFull = "";
    private bool _errorExpanded;

    private CancellationTokenSource? _detailsCts;

    private readonly PullSource? _pullSource;
    private bool _pullsLoading;
    private bool _pullBusy;

    public RepoPanel(RepoInfo repo)
    {
        Repo = repo;
        InitializeComponent();

        NameText.Text = repo.Name;
        ChipText.Text = repo.Remote.Chip;
        ChipBorder.Visibility = repo.Remote.Chip.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _pullSource = PullSource.From(repo.Remote);
        PullsToggle.Visibility = _pullSource is null ? Visibility.Collapsed : Visibility.Visible;

        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RequestRefresh();
        };

        GraphStyle = new GraphStyle(App.Settings.TreeColors(), _mainBranch);
        App.TreeColorsChanged += OnTreeColorsChanged;
        SizeChanged += (_, _) => UpdateGraphWidth();
        UpdateHeader();

        if (!App.GitAvailable)
        {
            ShowListMessage("git not found on PATH");
            FetchButton.IsEnabled = PullButton.IsEnabled = PushButton.IsEnabled = false;
            return;
        }

        StartWatchers();
        RequestRefresh();
        _ = LoadPullsAsync();
    }

    public event EventHandler? CloseRequested;

    public RepoInfo Repo { get; }

    /// <summary>"Fetch", "Pull" or "Push" while one runs in this panel, else null.</summary>
    public string? RunningCommand { get; private set; }

    public double GraphWidth
    {
        get => (double)GetValue(GraphWidthProperty);
        set => SetValue(GraphWidthProperty, value);
    }

    public GraphStyle? GraphStyle
    {
        get => (GraphStyle?)GetValue(GraphStyleProperty);
        set => SetValue(GraphStyleProperty, value);
    }

    public bool GraphClipped
    {
        get => (bool)GetValue(GraphClippedProperty);
        set => SetValue(GraphClippedProperty, value);
    }

    /// <summary>Cancels running git commands (kills their process trees) and stops watching.</summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        App.TreeColorsChanged -= OnTreeColorsChanged;
        _debounce.Stop();
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        _watchers.Clear();
        _detailsCts?.Cancel();
        _life.Cancel();
    }

    // ---- job queue (6.3) ----

    private void RequestRefresh()
    {
        if (_shutdown)
        {
            return;
        }

        if (_jobs > 0)
        {
            _refreshPending = true;
            return;
        }

        _ = RunJobAsync(RefreshAsync);
    }

    private async Task RunJobAsync(Func<CancellationToken, Task> job)
    {
        _jobs++;
        try
        {
            await _gate.WaitAsync(_life.Token);
            try
            {
                await job(_life.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowError(ex.Message, ex.Message, null);
        }
        finally
        {
            _jobs--;
        }

        if (_jobs == 0 && _refreshPending && !_shutdown)
        {
            _refreshPending = false;
            _ = RunJobAsync(RefreshAsync);
        }
    }

    private Task<GitResult> GitAsync(IEnumerable<string> args, bool network, CancellationToken ct) =>
        App.Git.RunAsync(Repo.Path, args, skipCredentialStore: network && Repo.AllCodeCommit, ct: ct);

    // ---- refresh ----

    private async Task RefreshAsync(CancellationToken ct)
    {
        var statusResult = await GitAsync(RepoStatus.Args, false, ct);
        var refsResult = await GitAsync(BranchTips.Args, false, ct);
        if (!statusResult.Ok)
        {
            ShowListMessage(GitErrors.Friendly(statusResult, Repo.Remote));
            return;
        }

        var status = RepoStatus.Parse(statusResult.StdOut);
        var refsText = refsResult.Ok ? refsResult.StdOut : "";
        var refLines = BranchTips.ParseLines(refsText);
        _status = status;
        _refLines = refLines;

        var fingerprint = status.Branch + "\n" + status.HeadHash + "\n" + refsText;
        if (fingerprint == _fingerprint)
        {
            UpdateHeader();
            return;
        }

        var main = BranchTips.MainBranch(refLines, status.Branch);
        List<GraphRow> rows = [];
        if (status.HeadHash is not null || refLines.Count > 0)
        {
            var log = await GitAsync(LogParser.Args, false, ct);
            if (!log.Ok)
            {
                ShowListMessage(GitErrors.Friendly(log, Repo.Remote));
                UpdateHeader();
                return;
            }

            var remotes = Repo.RemoteNames;
            rows = await Task.Run(() =>
            {
                var commits = LogParser.Parse(log.StdOut, remotes);
                var branchOf = BranchTips.FromLines(refLines, remotes);
                return LaneLayout.Build(commits, main, branchOf);
            }, ct);
        }

        ct.ThrowIfCancellationRequested();
        _fingerprint = fingerprint;
        _mainBranch = main;
        ApplyRows(rows);
        UpdateHeader();
    }

    private void ApplyRows(List<GraphRow> rows)
    {
        var style = new GraphStyle(App.Settings.TreeColors(), _mainBranch);
        var now = DateTimeOffset.Now;
        var items = rows.Select(r => CommitRowViewModel.For(r, style, now)).ToList();
        if (rows.Count >= LogParser.MaxCommits)
        {
            items.Add(CommitRowViewModel.Footer(LogParser.MaxCommits));
        }

        _maxSlots = rows.Count == 0 ? 1 : rows.Max(r => r.SlotCount);
        GraphStyle = style;
        UpdateGraphWidth();

        var selected = (CommitList.SelectedItem as CommitRowViewModel)?.Hash;
        var scroller = FindScrollViewer(CommitList);
        var offset = scroller?.VerticalOffset ?? 0;

        _rows = items;
        CommitList.ItemsSource = items;
        if (PullsToggle.IsChecked != true)
        {
            ListMessage.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListMessage.Text = items.Count == 0 ? "no commits yet" : "";
        }

        var keep = selected is null ? null : items.FirstOrDefault(i => i.Hash == selected);
        if (keep is not null)
        {
            CommitList.SelectedItem = keep;
        }
        else if (selected is not null)
        {
            CommitList.SelectedItem = null;
        }

        if (scroller is not null && offset > 0)
        {
            Dispatcher.BeginInvoke(() => scroller.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }
    }

    private void UpdateGraphWidth()
    {
        var wanted = GraphCell.LaneOffset + _maxSlots * GraphCell.LaneWidth;
        var cap = Math.Max(GraphCell.LaneOffset + GraphCell.LaneWidth, ActualWidth * 0.4);
        GraphWidth = Math.Min(wanted, cap);
        GraphClipped = wanted > cap;
    }

    private void OnTreeColorsChanged()
    {
        var style = new GraphStyle(App.Settings.TreeColors(), _mainBranch);
        GraphStyle = style;
        foreach (var row in _rows)
        {
            row.Recolour(style);
        }
    }

    private void ShowListMessage(string text)
    {
        ListMessage.Text = text;
        ListMessage.Visibility = Visibility.Visible;
    }

    // ---- header and buttons ----

    private (string Remote, string Name)? Upstream =>
        _status.Branch is { } b ? BranchTips.UpstreamOf(_refLines, b) : null;

    private bool HasRemote => Repo.RemoteNames.Count > 0;

    private void UpdateHeader()
    {
        var s = _status;
        BranchText.Text = s.IsDetached ? "detached" : s.Branch ?? "";
        SetBadge(AheadText, s.HasUpstream && s.Ahead > 0 ? $"↑{s.Ahead}" : null);
        SetBadge(BehindText, s.HasUpstream && s.Behind > 0 ? $"↓{s.Behind}" : null);
        SetBadge(DirtyText, s.Dirty > 0 ? $"●{s.Dirty}" : null);
        GoneText.Visibility = s.UpstreamGone ? Visibility.Visible : Visibility.Collapsed;
        DivergedText.Visibility = s.Diverged ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private static void SetBadge(TextBlock t, string? text)
    {
        t.Text = text ?? "";
        t.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateButtons()
    {
        var s = _status;
        var idle = RunningCommand is null && App.GitAvailable;
        FetchButton.IsEnabled = idle && HasRemote;
        PullButton.IsEnabled = idle && HasRemote && !s.IsDetached && s.HasUpstream;

        var up = Upstream;
        var localUpstream = up is { Remote: "." };
        PushButton.ToolTip = localUpstream ? "upstream is a local branch" : "Push";
        PushButton.IsEnabled = idle && HasRemote && !s.IsDetached && s.Branch is not null && s.HeadHash is not null
                               && !localUpstream && !(s.HasUpstream && s.Ahead == 0);
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync("Fetch", FetchButton, ct => GitAsync(["fetch", "--all", "--prune"], true, ct));

    private async void Pull_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync("Pull", PullButton, ct => GitAsync(["pull", "--ff-only", "--no-rebase"], true, ct));

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        var branch = _status.Branch;
        if (branch is null)
        {
            return;
        }

        string[] args;
        if (Upstream is { } up && _status.Upstream is not null)
        {
            if (up.Remote == ".")
            {
                return;
            }

            args = ["push", up.Remote, "HEAD:" + up.Name];
        }
        else
        {
            var remote = Repo.RemoteNames.Contains("origin") ? "origin" : Repo.RemoteNames.FirstOrDefault();
            if (remote is null)
            {
                return;
            }

            var ok = MessageBox.Show(Window.GetWindow(this)!, $"Publish {branch} to {remote}?", "logy",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes)
            {
                return;
            }

            args = ["push", "-u", remote, branch];
        }

        await RunCommandAsync("Push", PushButton, ct => GitAsync(args, true, ct));
    }

    private async Task<bool> RunCommandAsync(string name, Button button, Func<CancellationToken, Task<GitResult>> command)
    {
        if (RunningCommand is not null || _shutdown)
        {
            return false;
        }

        RunningCommand = name;
        StartSpinner(button);
        UpdateButtons();
        GitResult? result = null;
        await RunJobAsync(async ct =>
        {
            result = await command(ct);
            await RefreshAsync(ct);
        });

        StopSpinner();
        RunningCommand = null;
        if (result is not null)
        {
            if (result.Ok)
            {
                ClearFailure();
                if (name == "Fetch")
                {
                    _ = LoadPullsAsync();
                }
            }
            else
            {
                var friendly = GitErrors.Friendly(result, Repo.Remote);
                if (name == "Pull" && friendly == "branches diverged")
                {
                    friendly = "branches diverged: rebase or merge in a terminal";
                }

                var full = GitErrors.CleanStdErr(result.StdErr).Trim();
                ShowError(friendly, full.Length == 0 ? friendly : full, button);
            }
        }

        UpdateButtons();
        return result?.Ok == true;
    }

    private void StartSpinner(Button button)
    {
        _spinningButton = button;
        _spinningContent = button.Content;
        var glyph = new TextBlock
        {
            Text = "\uE895",
            FontFamily = (FontFamily)FindResource("Font.Icons"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        button.Content = glyph;
        button.IsEnabled = false;
        if (SystemParameters.ClientAreaAnimation)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
            glyph.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
    }

    private void StopSpinner()
    {
        if (_spinningButton is not null)
        {
            _spinningButton.Content = _spinningContent;
        }

        _spinningButton = null;
        _spinningContent = null;
    }

    private void ShowError(string shortText, string fullText, Button? button)
    {
        ClearFailure();
        if (button is not null)
        {
            _failedButton = button;
            button.Background = (Brush)FindResource("Brush.Danger");
        }

        _errorShort = shortText;
        _errorFull = fullText;
        _errorExpanded = false;
        ErrorText.Text = shortText;
        ErrorText.TextWrapping = TextWrapping.NoWrap;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void ClearFailure()
    {
        _failedButton?.ClearValue(BackgroundProperty);
        _failedButton = null;
        ErrorBar.Visibility = Visibility.Collapsed;
    }

    private void ErrorText_Click(object sender, MouseButtonEventArgs e)
    {
        _errorExpanded = !_errorExpanded;
        ErrorText.Text = _errorExpanded ? _errorFull : _errorShort;
        ErrorText.TextWrapping = _errorExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) => ErrorBar.Visibility = Visibility.Collapsed;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---- pull requests ----

    private void PullsToggle_Changed(object sender, RoutedEventArgs e)
    {
        var on = PullsToggle.IsChecked == true;
        PullView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CommitList.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            ListMessage.Visibility = Visibility.Collapsed;
            ShowPullPane();
            _ = LoadPullsAsync();
        }
        else
        {
            PullPane.Visibility = Visibility.Collapsed;
            ListMessage.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListMessage.Text = _rows.Count == 0 ? "no commits yet" : "";
            DetailsPane.Visibility = CommitList.SelectedItem is CommitRowViewModel ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Reads open pull requests and updates the PR button count and list. Keeps the selection by id.</summary>
    private async Task LoadPullsAsync(string? select = null)
    {
        if (_pullSource is null || _pullsLoading || _shutdown)
        {
            return;
        }

        _pullsLoading = true;
        if (PullsToggle.IsChecked == true && PullList.Items.Count == 0)
        {
            SetPullMessage("loading pull requests…");
        }

        try
        {
            var keep = select ?? (PullList.SelectedItem as PullRowViewModel)?.Pull.Id;
            var result = await App.Pulls.LoadAsync(_pullSource, Repo.Path, _life.Token);
            var now = DateTimeOffset.Now;
            var rows = result.Pulls.Select(p => new PullRowViewModel(p, now)).ToList();
            PullList.ItemsSource = rows;
            PullList.SelectedItem = rows.FirstOrDefault(r => r.Pull.Id == keep);
            PullsToggleText.Text = result.Error is null && result.Pulls.Count > 0 ? $"PR {result.Pulls.Count}" : "PR";
            PullsToggle.ToolTip = result.Error is null
                ? $"{result.Pulls.Count} open pull request{(result.Pulls.Count == 1 ? "" : "s")}"
                : "pull requests: " + result.Error;
            SetPullMessage(result.Error ?? (result.Pulls.Count == 0 ? "no open pull requests" : null));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pullsLoading = false;
        }
    }

    private void SetPullMessage(string? text)
    {
        PullMessage.Text = text ?? "";
        PullMessage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PullList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPullPane();

    private void PullList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PullList.SelectedItem = null;
            e.Handled = true;
        }
    }

    private PullRequest? SelectedPull => (PullList.SelectedItem as PullRowViewModel)?.Pull;

    private void ShowPullPane()
    {
        var pr = SelectedPull;
        if (pr is null || PullsToggle.IsChecked != true || _pullSource is null)
        {
            PullPane.Visibility = Visibility.Collapsed;
            return;
        }

        PullPane.Visibility = Visibility.Visible;
        PullTitle.Text = $"#{pr.Id} {pr.Title}";
        PullLine2.Text = string.Join(" · ", new[] { pr.Author, $"{pr.SourceBranch} → {pr.TargetBranch}" }.Where(x => x.Trim().Length > 1));
        PullDescription.Text = pr.Description.Trim();
        PullDescription.Visibility = pr.Description.Trim().Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        MergeButtons.Children.Clear();
        foreach (var m in MergeMethods.For(_pullSource.Kind))
        {
            var first = MergeButtons.Children.Count == 0;
            var b = new Button
            {
                Content = first ? $"Merge ({MergeMethods.Label(m)})" : MergeMethods.Label(m),
                Style = (Style)FindResource(first ? "AccentTextButton" : "TextButton"),
                Height = 24,
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = $"Merge into {pr.TargetBranch} with {MergeMethods.Label(m)}",
            };
            b.Click += async (_, _) => await MergeAsync(m);
            MergeButtons.Children.Add(b);
        }

        SetPullActionsEnabled(!_pullBusy);
    }

    private void SetPullActionsEnabled(bool on)
    {
        NewPullButton.IsEnabled = on;
        ApproveButton.IsEnabled = on;
        foreach (var b in MergeButtons.Children.OfType<Button>())
        {
            b.IsEnabled = on;
        }
    }

    private void OpenPull_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPull is { } pr && pr.WebUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pr.WebUrl) { UseShellExecute = true });
        }
    }

    /// <summary>Remote the pull requests live on: origin, else the first remote (same rule as the chip).</summary>
    private string? PullRemote => Repo.RemoteNames.Contains("origin") ? "origin" : Repo.RemoteNames.FirstOrDefault();

    private string? LocalHash(string branch) => _refLines.FirstOrDefault(l => l.RefName == "refs/heads/" + branch)?.ObjectName;

    private async void NewPull_Click(object sender, RoutedEventArgs e)
    {
        if (_pullSource is null || PullRemote is not { } remote || _pullBusy)
        {
            return;
        }

        var locals = _refLines.Where(l => l.RefName.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(l => l.RefName["refs/heads/".Length..]).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var prefix = $"refs/remotes/{remote}/";
        var targets = _refLines.Where(l => l.RefName.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l.RefName[prefix.Length..]).Where(x => x != "HEAD").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (locals.Count == 0 || targets.Count == 0)
        {
            ShowError($"no branches on {remote} yet: push a branch first", $"no branches on {remote} yet: push a branch first", null);
            return;
        }

        var target = new[] { "main", "master" }.FirstOrDefault(targets.Contains) ?? targets[0];
        var source = _status.Branch is { } cur && cur != target ? cur : locals.FirstOrDefault(b => b != target);
        string TitleFor(string b) =>
            LocalHash(b) is { } h ? _rows.FirstOrDefault(r => r.Hash == h)?.Subject ?? "" : "";

        var input = CreatePullWindow.Ask(Window.GetWindow(this)!, locals, targets, source, target, remote, TitleFor);
        if (input is null)
        {
            return;
        }

        // The pull request is built from the remote branch, so it must exist there and match local.
        var local = LocalHash(input.Source);
        var onRemote = _refLines.FirstOrDefault(l => l.RefName == prefix + input.Source)?.ObjectName;
        if (local is not null && local != onRemote)
        {
            var what = onRemote is null ? $"{input.Source} is not on {remote} yet." : $"{input.Source} has commits that are not on {remote}.";
            if (MessageBox.Show(Window.GetWindow(this)!, $"{what} Push it first?", "logy", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            {
                return;
            }

            var hasUpstream = _refLines.Any(l => l.RefName == "refs/heads/" + input.Source && l.UpstreamRemote.Length > 0);
            string[] args = hasUpstream
                ? ["push", remote, $"{input.Source}:{input.Source}"]
                : ["push", "-u", remote, input.Source];
            if (!await RunCommandAsync("Push", PushButton, ct => GitAsync(args, true, ct)))
            {
                return;
            }
        }

        await PullActionAsync("creating pull request…", async ct =>
        {
            var r = await App.Pulls.CreateAsync(_pullSource, Repo.Path, input.Source, input.Target, input.Title, input.Description, ct);
            if (r.Ok)
            {
                PullStatus.Text = r.Pull is { } p ? $"created #{p.Id}" : "created";
                await LoadPullsAsync(r.Pull?.Id);
            }

            return r;
        });
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_pullSource is null || SelectedPull is not { } pr || _pullBusy)
        {
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!, $"Approve #{pr.Id} \"{pr.Title}\"?", "logy", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        await PullActionAsync("approving…", async ct =>
        {
            var r = await App.Pulls.ApproveAsync(_pullSource, Repo.Path, pr, ct);
            if (r.Ok)
            {
                PullStatus.Text = $"approved #{pr.Id}";
            }

            return r;
        });
    }

    private async Task MergeAsync(MergeMethod method)
    {
        if (_pullSource is null || SelectedPull is not { } pr || _pullBusy)
        {
            return;
        }

        var q = $"Merge #{pr.Id} \"{pr.Title}\" into {pr.TargetBranch} ({MergeMethods.Label(method)})?\n\nThis changes {pr.TargetBranch} on the remote.";
        if (MessageBox.Show(Window.GetWindow(this)!, q, "logy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var merged = false;
        await PullActionAsync("merging…", async ct =>
        {
            var r = await App.Pulls.MergeAsync(_pullSource, Repo.Path, pr, method, ct);
            if (r.Ok)
            {
                merged = true;
                PullStatus.Text = $"merged #{pr.Id} into {pr.TargetBranch}";
                PullList.SelectedItem = null;
                await LoadPullsAsync();
            }

            return r;
        });

        if (merged && HasRemote)
        {
            // Bring the merge into the tree.
            await RunCommandAsync("Fetch", FetchButton, ct => GitAsync(["fetch", "--all", "--prune"], true, ct));
        }
    }

    /// <summary>One pull request write at a time per panel; failures go to the error bar.</summary>
    private async Task PullActionAsync(string busyText, Func<CancellationToken, Task<PullActionResult>> action)
    {
        _pullBusy = true;
        SetPullActionsEnabled(false);
        PullStatus.Text = busyText;
        try
        {
            var r = await action(_life.Token);
            if (r.Ok)
            {
                ClearFailure();
            }
            else
            {
                PullStatus.Text = "";
                ShowError(r.Error!, r.Error!, null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pullBusy = false;
            SetPullActionsEnabled(true);
        }
    }

    // ---- selection and details ----

    private void CommitList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the selected row again clears the selection and hides the pane.
        if (e.OriginalSource is DependencyObject src
            && ItemsControl.ContainerFromElement(CommitList, src) is ListBoxItem { IsSelected: true })
        {
            CommitList.SelectedItem = null;
            e.Handled = true;
        }
    }

    private void CommitList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CommitList.SelectedItem = null;
            e.Handled = true;
        }
    }

    private async void CommitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _detailsCts?.Cancel();
        if (CommitList.SelectedItem is not CommitRowViewModel { IsFooter: false } row)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _detailsCts = cts;
        DetailsPane.Visibility = Visibility.Visible;
        DetailsMessage.Text = "loading…";
        DetailsFiles.Text = "";
        try
        {
            var hash = row.Hash;
            var msg = await GitAsync(["show", "--no-patch", "--format=%B", hash], false, cts.Token);
            var files = await GitAsync(
                ["diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-m", "--first-parent", hash], false, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            DetailsMessage.Text = msg.Ok ? msg.StdOut.Trim() : GitErrors.Friendly(msg, Repo.Remote);
            DetailsFiles.Text = files.Ok
                ? string.Join('\n', files.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Replace('\t', ' ')))
                : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DetailsMessage.Text = ex.Message;
        }
    }

    // ---- live refresh (6.3) ----

    private void StartWatchers()
    {
        AddWatcher(Repo.GitDir);
        if (Repo.CommonDir is { } common && !string.Equals(common, Repo.GitDir, StringComparison.OrdinalIgnoreCase))
        {
            AddWatcher(common);
        }
    }

    private void AddWatcher(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Changed += (_, e) => OnFsEvent(dir, e.FullPath);
            w.Created += (_, e) => OnFsEvent(dir, e.FullPath);
            w.Deleted += (_, e) => OnFsEvent(dir, e.FullPath);
            w.Renamed += (_, e) => OnFsEvent(dir, e.FullPath);
            w.Error += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No live refresh for this dir; buttons still refresh.
        }
    }

    /// <summary>True for HEAD, index, packed-refs, FETCH_HEAD, ORIG_HEAD and anything under refs/; never *.lock or objects/.</summary>
    internal static bool Counts(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("objects/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rel is "HEAD" or "index" or "packed-refs" or "FETCH_HEAD" or "ORIG_HEAD"
               || rel.StartsWith("refs/", StringComparison.Ordinal);
    }

    private void OnFsEvent(string root, string fullPath)
    {
        if (Counts(root, fullPath))
        {
            Kick();
        }
    }

    private void Kick() => Dispatcher.BeginInvoke(() =>
    {
        if (_shutdown)
        {
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    });

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv)
        {
            return sv;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
