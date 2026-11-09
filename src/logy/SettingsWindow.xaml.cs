using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Logy.Core.Theming;
using Microsoft.Win32;

namespace Logy;

/// <summary>Root folder, accent and tree colours (design doc 6.5). Every change applies live and is saved.</summary>
public partial class SettingsWindow : Window
{
    private int _selected;
    private bool _syncing;

    public SettingsWindow()
    {
        InitializeComponent();
        Native.DwmHelper.Apply(this);
        VersionText.Text = $"logy {App.Version}";
        RootBox.Text = App.Settings.RootFolder;
        _syncing = true;
        AccentBox.Text = App.Settings.AccentOrDefault();
        _syncing = false;
        BuildAccentSwatches();
        BuildTree();
    }

    private static Brush B(string hex) => App.Brush(hex);

    // ---- root folder ----

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Root folder with your repos",
            InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : "",
        };
        if (dlg.ShowDialog(this) == true)
        {
            RootBox.Text = dlg.FolderName;
            SaveRoot();
        }
    }

    private void RootBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveRoot();

    private void RootBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveRoot();
        }
    }

    private void SaveRoot()
    {
        var path = RootBox.Text.Trim();
        if (path.Length == 0 || !Directory.Exists(path))
        {
            RootBox.Text = App.Settings.RootFolder;
            return;
        }

        if (path != App.Settings.RootFolder)
        {
            App.UpdateSettings(s => s.RootFolder = path);
        }
    }

    // ---- accent ----

    private void BuildAccentSwatches()
    {
        var current = App.Settings.AccentOrDefault();
        var items = new List<UIElement>();
        foreach (var hex in Palettes.Accents)
        {
            var on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            items.Add(Swatch(hex, on, () => SetAccent(hex, updateBox: true)));
        }

        AccentSwatches.ItemsSource = items;
    }

    private void AccentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && Palettes.IsValid(AccentBox.Text))
        {
            SetAccent(Normalize(AccentBox.Text), updateBox: false);
        }
    }

    private void SetAccent(string hex, bool updateBox)
    {
        App.UpdateSettings(s => s.Accent = hex);
        App.ApplyAccent(hex);
        if (updateBox)
        {
            _syncing = true;
            AccentBox.Text = hex;
            _syncing = false;
        }

        BuildAccentSwatches();
        BuildTree();
    }

    // ---- tree colours ----

    private void BuildTree()
    {
        var s = App.Settings;
        var isCustom = string.Equals(s.TreePreset, Palettes.CustomName, StringComparison.OrdinalIgnoreCase);
        PresetPanel.Children.Clear();
        foreach (var p in Palettes.Presets)
        {
            var active = !isCustom && string.Equals(p.Name, s.TreePreset, StringComparison.OrdinalIgnoreCase);
            PresetPanel.Children.Add(PresetButton(p.Name, active, () =>
            {
                App.UpdateSettings(x => x.TreePreset = p.Name);
                TreeChanged();
            }));
        }

        PresetPanel.Children.Add(PresetButton("Random", false, () =>
        {
            var colors = Palettes.Random(new Random(), 8);
            App.UpdateSettings(x =>
            {
                x.CustomColors = colors;
                x.TreePreset = Palettes.CustomName;
            });
            TreeChanged();
        }));

        if (isCustom)
        {
            PresetPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("Brush.Accent"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 0, 12, 0),
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock
                {
                    Text = "Custom",
                    Foreground = (Brush)FindResource("Brush.OnAccent"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var current = s.TreeColors();
        _selected = Math.Clamp(_selected, 0, current.Count - 1);
        ColorSquares.Children.Clear();
        for (var i = 0; i < current.Count; i++)
        {
            var index = i;
            ColorSquares.Children.Add(Swatch(current[i], i == _selected, () =>
            {
                _selected = index;
                BuildTree();
            }));
        }

        _syncing = true;
        TreeBox.Text = current[_selected];
        _syncing = false;
    }

    private void TreeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !Palettes.IsValid(TreeBox.Text))
        {
            return;
        }

        var hex = Normalize(TreeBox.Text);
        var colors = App.Settings.TreeColors().ToList();
        if (_selected >= colors.Count || string.Equals(colors[_selected], hex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        colors[_selected] = hex;
        App.UpdateSettings(x =>
        {
            x.CustomColors = colors;
            x.TreePreset = Palettes.CustomName;
        });
        var caret = TreeBox.CaretIndex;
        TreeChanged();
        TreeBox.CaretIndex = Math.Min(caret, TreeBox.Text.Length);
    }

    private void TreeChanged()
    {
        BuildTree();
        App.RaiseTreeColorsChanged();
    }

    // ---- building blocks ----

    /// <summary>A 22 px solid square; the selected one gets the 2 px white gap ring.</summary>
    private UIElement Swatch(string hex, bool selected, Action click)
    {
        var inner = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = B(hex) };
        var ring = new Border
        {
            Padding = new Thickness(2),
            BorderThickness = new Thickness(2),
            BorderBrush = selected ? B("#FFFFFF") : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 2, 0),
            Child = inner,
            Cursor = Cursors.Hand,
            ToolTip = hex,
            Background = Brushes.Transparent,
        };
        ring.MouseLeftButtonUp += (_, _) => click();
        return ring;
    }

    private Button PresetButton(string name, bool active, Action click)
    {
        var b = new Button
        {
            Content = name,
            Style = (Style)FindResource(active ? "AccentTextButton" : "TextButton"),
            Margin = new Thickness(0, 0, 6, 6),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static string Normalize(string hex)
    {
        var h = hex.Trim().TrimStart('#').ToUpperInvariant();
        return "#" + h;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveRoot();
        base.OnClosed(e);
    }
}
