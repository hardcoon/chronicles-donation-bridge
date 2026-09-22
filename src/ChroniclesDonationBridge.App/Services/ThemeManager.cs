using System.Windows;
using System.Windows.Media;

namespace ChroniclesDonationBridge.App.Services;

internal static class ThemeManager
{
    private static readonly ThemeColor[] Palette =
    [
        new("BackgroundBrush", "#181B1F", "#F2F5F7"),
        new("SurfaceBrush", "#1E2329", "#FFFFFF"),
        new("PanelBrush", "#252B32", "#E8EEF3"),
        new("PanelAltBrush", "#2D353E", "#DCE6ED"),
        new("InputBrush", "#171B20", "#FFFFFF"),
        new("BorderBrush", "#3A4652", "#C5D0DA"),
        new("BorderStrongBrush", "#536577", "#9DAFBE"),
        new("TextBrush", "#F1F4F7", "#202933"),
        new("MutedBrush", "#B4C0CA", "#536575"),
        new("SubtleBrush", "#8E9CAA", "#6A7A89"),
        new("AccentBrush", "#6FA9D8", "#3978A6"),
        new("AccentForegroundBrush", "#9AC6E8", "#2F658D"),
        new("AccentTextBrush", "#101820", "#FFFFFF"),
        new("BlueBrush", "#78B6E6", "#3978A6"),
        new("GoodBrush", "#65D39A", "#117946"),
        new("GoodDarkBrush", "#173E32", "#DDF3E7"),
        new("BadBrush", "#FF8585", "#B22F38"),
        new("BadDarkBrush", "#48272B", "#F8DFE1"),

        new("SelectionBrush", "#80527CA0", "#804A7EA7"),
        new("SelectionSurfaceBrush", "#35546B", "#CFE2F0"),
        new("SelectionTextBrush", "#FFFFFF", "#173044"),
        new("ComboSideBrush", "#222931", "#E6EDF2"),
        new("ControlHoverBrush", "#303A45", "#DFE9F0"),
        new("ControlHoverBorderBrush", "#6E8BA4", "#82A4BF"),
        new("ComboItemHoverBrush", "#2E3944", "#E1EAF1"),
        new("ComboItemSelectedBrush", "#36566E", "#CFE1EE"),
        new("ScrollTrackBrush", "#1B2026", "#E8EDF1"),
        new("ScrollThumbBrush", "#526A7D", "#9AADB9"),
        new("ScrollThumbHoverBrush", "#6D8CA5", "#7896AA"),
        new("SuccessTextBrush", "#CCF7DE", "#155B3C"),
        new("SuccessBorderBrush", "#2B7458", "#6FB38F"),
        new("DangerTextBrush", "#FFD6D6", "#8A2930"),
        new("DangerBorderBrush", "#7A3B46", "#D59196"),
        new("SwitchTrackBrush", "#3D4751", "#C6D0D8"),
        new("SwitchBorderBrush", "#5B6977", "#9EAFBB"),
        new("SwitchThumbBrush", "#D8E1E8", "#FFFFFF"),
        new("SwitchOnBrush", "#4F7FA5", "#4B82AC"),
        new("SwitchOnThumbBrush", "#FFFFFF", "#FFFFFF"),
        new("TabHoverBrush", "#293540", "#E0EAF1"),
        new("DataGridHoverBrush", "#29343E", "#E3EBF1"),
        new("DataGridSelectedBrush", "#36576F", "#CFE1EE"),

        new("StatusCardBrush", "#20262C", "#FFFFFF"),
        new("ListWellBrush", "#181B1F", "#F2F5F7"),
        new("ActionRowEvenBrush", "#242B32", "#FFFFFF"),
        new("ActionRowOddBrush", "#29323A", "#EFF4F7"),
        new("ActionRowBorderBrush", "#3B4854", "#C7D2DA"),
        new("ActionRowMarkerBrush", "#6FA9D8", "#4C83AD"),
        new("ActionExpanderBrush", "#2B343D", "#E2EAF0"),
        new("ActionExpanderHoverBrush", "#34414D", "#D8E4EC"),
        new("ActionExpanderOpenBrush", "#3A4B59", "#CEDFEA"),
        new("TriggerChipBrush", "#2A3945", "#DCEAF3"),
        new("CountBadgeBrush", "#334B5D", "#D3E3ED"),

        new("InfoPanelBrush", "#22313D", "#E5F0F7"),
        new("InfoTextBrush", "#C7D9E8", "#3E5C72"),
        new("WarningPanelBrush", "#332D24", "#F6F0E5"),
        new("WarningBorderBrush", "#7D6B4A", "#C5A96D"),
        new("WarningBadgeBrush", "#514532", "#EEE1C5"),
        new("WarningTextBrush", "#E4D0A4", "#6D572E")
    ];

    public static bool IsLightTheme { get; private set; }
    public static event Action? ThemeChanged;

    public static void Apply(bool useLightTheme)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("WPF Application is not initialized.");
        if (!application.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The application theme must be changed on the UI thread.");
        }

        foreach (var entry in Palette)
        {
            var colorKey = entry.Key.EndsWith("Brush", StringComparison.Ordinal)
                ? entry.Key[..^"Brush".Length] + "Color"
                : throw new InvalidOperationException($"Theme resource {entry.Key} is not a brush key.");
            if (!application.Resources.Contains(colorKey))
            {
                throw new InvalidOperationException($"Theme color {colorKey} is missing.");
            }
            var color = Parse(useLightTheme ? entry.Light : entry.Dark);
            application.Resources[colorKey] = color;
            application.Resources[entry.Key] = new SolidColorBrush(color);
        }

        IsLightTheme = useLightTheme;
        ThemeChanged?.Invoke();
    }

    private static Color Parse(string value) =>
        ColorConverter.ConvertFromString(value) is Color color
            ? color
            : throw new InvalidOperationException($"Invalid theme color {value}.");

    private sealed record ThemeColor(string Key, string Dark, string Light);
}
