using System.Globalization;

namespace Logy.Core.Theming;

/// <summary>A named set of branch colours for the tree.</summary>
public sealed record Palette(string Name, IReadOnlyList<string> Colors);

/// <summary>Tree colour presets, the random generator and text-contrast helper. Colours are "#RRGGBB".</summary>
public static class Palettes
{
    public const string CustomName = "Custom";

    public static readonly IReadOnlyList<Palette> Presets =
    [
        new("Fluent", ["#0078D4", "#E3008C", "#10893E", "#F7630C", "#8764B8", "#FFB900", "#C42B1C", "#7A7574"]),
        new("Vivid", ["#3D8BFF", "#FF4D6D", "#3DDC84", "#FFB000", "#C86BFF", "#FF7A3D", "#FFE14D", "#FF6EC7"]),
        new("Pastel", ["#9ECBFF", "#FFB3C7", "#B5E8A1", "#FFD59E", "#CDB8FF", "#FFE9A3", "#F5B4A8", "#C9D6A3"]),
        new("Earth", ["#C8783C", "#8A9A5B", "#D4A24C", "#6B8E9E", "#A0522D", "#9C7A54", "#7D6B91", "#B5651D"]),
        new("Mono", ["#FFFFFF", "#C8C8C8", "#9A9A9A", "#707070", "#E0E0E0", "#B0B0B0", "#858585", "#5C5C5C"]),
    ];

    /// <summary>Accent colours offered in settings; white is the default.</summary>
    public static readonly IReadOnlyList<string> Accents =
        ["#FFFFFF", "#0078D4", "#10893E", "#F7630C", "#C42B1C", "#8764B8"];

    public const string DefaultAccent = "#FFFFFF";

    public static Palette Default => Presets[0];

    /// <summary>The preset called <paramref name="name"/>, or the default preset.</summary>
    public static Palette Get(string? name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>
    /// <paramref name="count"/> solid, well-separated colours: hues stepped by the golden angle
    /// from a random start, with saturation and lightness kept in a readable band on dark grey.
    /// </summary>
    public static List<string> Random(Random rng, int count = 8)
    {
        var start = rng.NextDouble() * 360;
        var colors = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var hue = (start + i * 137.508) % 360;
            colors.Add(FromHsl(hue, 0.70 + rng.NextDouble() * 0.20, 0.52 + rng.NextDouble() * 0.12));
        }

        return colors;
    }

    /// <summary>Text colour that stays readable on <paramref name="background"/>: dark on light colours, white on dark.</summary>
    public static string Ink(string background)
    {
        if (!TryParse(background, out var r, out var g, out var b))
        {
            return "#FFFFFF";
        }

        var luma = 0.299 * r + 0.587 * g + 0.114 * b;
        return luma > 150 ? "#202020" : "#FFFFFF";
    }

    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is null)
        {
            return false;
        }

        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    public static bool IsValid(string? hex) => TryParse(hex, out _, out _, out _);

    /// <summary>Per channel <c>round(a + (b - a) * t)</c>. Invalid input returns <paramref name="a"/> unchanged.</summary>
    public static string Blend(string a, string b, double t)
    {
        if (!TryParse(a, out var ar, out var ag, out var ab) || !TryParse(b, out var br, out var bg, out var bb))
        {
            return a;
        }

        static int Mix(byte x, byte y, double t) =>
            (int)Math.Clamp(Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero), 0, 255);

        return $"#{Mix(ar, br, t):X2}{Mix(ag, bg, t):X2}{Mix(ab, bb, t):X2}";
    }

    /// <summary>HSL (hue 0-360, saturation and lightness 0-1) to "#RRGGBB".</summary>
    public static string FromHsl(double h, double s, double l)
    {
        double F(double n)
        {
            var k = (n + h / 30) % 12;
            var a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }

        static int To255(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{To255(F(0)):X2}{To255(F(8)):X2}{To255(F(4)):X2}";
    }
}
