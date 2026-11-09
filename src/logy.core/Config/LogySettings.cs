using Logy.Core.Theming;

namespace Logy.Core.Config;

/// <summary>Everything logy remembers between runs. Stored as settings.json in <see cref="JsonStore.DefaultFolder"/>.</summary>
public sealed class LogySettings
{
    public const string FileName = "settings.json";

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "git");

    /// <summary>Folder whose direct subfolders are listed as repos.</summary>
    public string RootFolder { get; set; } = DefaultRoot;

    /// <summary>Accent for Push, +, the pin-on state and the active preset. "#RRGGBB".</summary>
    public string Accent { get; set; } = Palettes.DefaultAccent;

    /// <summary>Preset name, or <see cref="Palettes.CustomName"/> to use <see cref="CustomColors"/>.</summary>
    public string TreePreset { get; set; } = Palettes.Default.Name;

    /// <summary>The user's own tree colours (edited or randomly generated). Kept when a preset is picked.</summary>
    public List<string> CustomColors { get; set; } = [];

    /// <summary>Full paths of the open panels, left to right.</summary>
    public List<string> OpenRepos { get; set; } = [];

    public bool AlwaysOnTop { get; set; }

    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = 960;

    public double Height { get; set; } = 600;

    /// <summary>The colours the tree draws with right now.</summary>
    public IReadOnlyList<string> TreeColors()
    {
        if (string.Equals(TreePreset, Palettes.CustomName, StringComparison.OrdinalIgnoreCase))
        {
            var valid = CustomColors.Where(Palettes.IsValid).ToList();
            if (valid.Count > 0)
            {
                return valid;
            }
        }

        return Palettes.Get(TreePreset).Colors;
    }

    public string AccentOrDefault() => Palettes.IsValid(Accent) ? Accent : Palettes.DefaultAccent;
}
