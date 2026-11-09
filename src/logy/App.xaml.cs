using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Logy.Core.Config;
using Logy.Core.Git;
using Logy.Core.Theming;
using Velopack;
using Velopack.Sources;

namespace Logy;

public partial class App : Application
{
    private const string RepositoryUrl = "https://github.com/bara-bba/logy";

    public static SettingsStore Store { get; } = new(new JsonStore(JsonStore.DefaultFolder));

    /// <summary>In-memory copy; every change also goes through <see cref="Store"/> (D15).</summary>
    public static LogySettings Settings { get; private set; } = new();

    public static GitRunner Git { get; } = new();

    /// <summary>False when <c>git --version</c> failed at startup.</summary>
    public static bool GitAvailable { get; private set; } = true;

    /// <summary>Raised after the tree colours change, so open panels recolour live.</summary>
    public static event Action? TreeColorsChanged;

    public static string Version
    {
        get
        {
            var v = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = Store.Load();
        ApplyAccent(Settings.AccentOrDefault());
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "logy", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        GitAvailable = Git.IsAvailableAsync().GetAwaiter().GetResult();
        if (!GitAvailable)
        {
            MessageBox.Show("git not found on PATH", "logy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _ = CheckForUpdatesAsync(window);
    }

    /// <summary>Saves only the fields <paramref name="change"/> touches and mirrors them in memory.</summary>
    public static void UpdateSettings(Action<LogySettings> change)
    {
        change(Settings);
        try
        {
            Store.Update(change);
        }
        catch (IOException)
        {
            // Settings are a convenience; never crash the app because the file is locked.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void RaiseTreeColorsChanged() => TreeColorsChanged?.Invoke();

    /// <summary>Swaps the accent brushes (design doc 6.5); everything bound with DynamicResource follows.</summary>
    public static void ApplyAccent(string hex)
    {
        var ink = Palettes.Ink(hex);
        var hover = ink == "#202020" ? Palettes.Blend(hex, "#000000", 0.12) : Palettes.Blend(hex, "#FFFFFF", 0.12);
        Current.Resources["Brush.Accent"] = Brush(hex);
        Current.Resources["Brush.OnAccent"] = Brush(ink);
        Current.Resources["Brush.AccentHover"] = Brush(hover);
        Current.Resources["Brush.AccentPressed"] = Brush(Palettes.Blend(hex, "#000000", 0.24));
    }

    public static SolidColorBrush Brush(string hex)
    {
        Palettes.TryParse(hex, out var r, out var g, out var b);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>One check per start (D11). Not installed via Velopack, offline or no release: silent.</summary>
    private static async Task CheckForUpdatesAsync(Window owner)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            if (!manager.IsInstalled)
            {
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            if (!UpdatePromptWindow.Ask(owner, version))
            {
                return;
            }

            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception)
        {
            // Errors are ignored silently (design doc 6.6).
        }
    }
}
