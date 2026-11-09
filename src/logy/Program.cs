using Velopack;

namespace Logy;

/// <summary>
/// Explicit entry point (selected with &lt;StartupObject&gt;), replacing the one WPF generates from
/// App.xaml. Velopack has to run before any window exists: on the first launch after an install or
/// update it does housekeeping and may exit the process. No single-instance lock: several logy
/// windows may run at once.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // No-op when not running from a Velopack install (e.g. straight out of bin/).
        VelopackApp.Build().Run();

        var app = new App();

        // Required with a hand-written Main: loads App.xaml's merged dictionaries (Theme.xaml).
        app.InitializeComponent();
        app.Run();
    }
}
