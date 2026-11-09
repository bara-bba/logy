using System.Windows;

namespace Logy;

/// <summary>logy's own small update prompt: Update or Later, nothing else (design doc 6.6).</summary>
public partial class UpdatePromptWindow : Window
{
    private UpdatePromptWindow(string version)
    {
        InitializeComponent();
        Native.DwmHelper.Apply(this);
        MessageText.Text = $"logy {version} is available";
    }

    /// <summary>True when the user pressed Update.</summary>
    public static bool Ask(Window owner, string version)
    {
        var w = new UpdatePromptWindow(version) { Owner = owner };
        return w.ShowDialog() == true;
    }

    private void Update_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
