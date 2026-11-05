using Avalonia;
using Avalonia.Media;

namespace EggmansReleaseManager.UI;

public sealed record Palette(
    string Id, string Name, string Blurb,
    string Bg, string Panel, string Frame, string FrameHi,
    string Fg, string Dim, string Accent, string Accent2, string Hi,
    string Ok, string Warn, string Err,
    string SelBg, string SelFg,
    string LogoTop, string LogoMid, string LogoBottom);

/// <summary>Swaps the whole colour scheme at runtime by replacing brush resources (all XAML uses DynamicResource).</summary>
public static class ThemeManager
{
    public static readonly IReadOnlyList<Palette> Palettes =
    [
        new("ansi", "ANSi CLASSiC", "16-colour DOS/BBS palette, blue frames, cyan + magenta",
            Bg: "#000000", Panel: "#05050F", Frame: "#2A2AB8", FrameHi: "#5555FF",
            Fg: "#C0C0C0", Dim: "#6E6E6E", Accent: "#55FFFF", Accent2: "#FF55FF", Hi: "#FFFF55",
            Ok: "#55FF55", Warn: "#FFFF55", Err: "#FF5555",
            SelBg: "#0000AA", SelFg: "#FFFFFF",
            LogoTop: "#55FFFF", LogoMid: "#5555FF", LogoBottom: "#FF55FF"),

        new("amber", "AMBER TERMiNAL", "the v3 orange, now on a monochrome amber CRT",
            Bg: "#0B0600", Panel: "#120A00", Frame: "#7A4300", FrameHi: "#CC7A00",
            Fg: "#FFB000", Dim: "#8A5C00", Accent: "#FFCC55", Accent2: "#FF9900", Hi: "#FFE6A0",
            Ok: "#FFD27A", Warn: "#FF9900", Err: "#FF5A3C",
            SelBg: "#FFB000", SelFg: "#0B0600",
            LogoTop: "#FFE6A0", LogoMid: "#FFB000", LogoBottom: "#B35C00"),

        new("phosphor", "GREEN PHOSPHOR", "P1 phosphor VT terminal, glowing green",
            Bg: "#000A02", Panel: "#001205", Frame: "#0A5C1E", FrameHi: "#22B14C",
            Fg: "#33FF66", Dim: "#1A8033", Accent: "#99FFAA", Accent2: "#00CC44", Hi: "#CCFFD5",
            Ok: "#99FFAA", Warn: "#E6FF66", Err: "#FF6655",
            SelBg: "#33FF66", SelFg: "#001205",
            LogoTop: "#CCFFD5", LogoMid: "#33FF66", LogoBottom: "#0A7A28"),

        new("acid", "ACiD TRiP", "1990s art-pack energy: hot pink, purple and lime",
            Bg: "#07000D", Panel: "#0E0018", Frame: "#5A1A8C", FrameHi: "#B055FF",
            Fg: "#E0D0FF", Dim: "#7A6A99", Accent: "#AAFF00", Accent2: "#FF3DCB", Hi: "#FFF04D",
            Ok: "#AAFF00", Warn: "#FFF04D", Err: "#FF3D5E",
            SelBg: "#FF3DCB", SelFg: "#07000D",
            LogoTop: "#FFF04D", LogoMid: "#FF3DCB", LogoBottom: "#5A1AFF"),
    ];

    public static Palette Current { get; private set; } = Palettes[0];

    public static Palette Get(string? id) =>
        Palettes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];

    public static IBrush Brush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var v) == true && v is IBrush b ? b : Brushes.Gray;

    public static void Apply(string? id, string fontFamily, double fontSize)
    {
        var p = Get(id);
        Current = p;
        var r = Application.Current!.Resources;

        static SolidColorBrush B(string hex) => new(Color.Parse(hex));
        static Color C(string hex) => Color.Parse(hex);

        // Our own keys
        r["BgBrush"] = B(p.Bg);
        r["PanelBrush"] = B(p.Panel);
        r["FrameBrush"] = B(p.Frame);
        r["FrameHiBrush"] = B(p.FrameHi);
        r["FgBrush"] = B(p.Fg);
        r["DimBrush"] = B(p.Dim);
        r["AccentBrush"] = B(p.Accent);
        r["Accent2Brush"] = B(p.Accent2);
        r["HiBrush"] = B(p.Hi);
        r["OkBrush"] = B(p.Ok);
        r["WarnBrush"] = B(p.Warn);
        r["ErrBrush"] = B(p.Err);
        r["SelBgBrush"] = B(p.SelBg);
        r["SelFgBrush"] = B(p.SelFg);
        r["LogoBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(C(p.LogoTop), 0.0),
                new GradientStop(C(p.LogoMid), 0.55),
                new GradientStop(C(p.LogoBottom), 1.0),
            },
        };
        r["ScanlineBrush"] = new SolidColorBrush(Colors.Black, 0.28);

        r["MonoFont"] = new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Cascadia Mono, Consolas" : fontFamily);
        r["BaseFontSize"] = Math.Clamp(fontSize, 9, 24);

        // Fluent theme keys (TextBox, menus, tooltips, scrollbars) so stock controls match.
        r["SystemAccentColor"] = C(p.Accent);
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
            r[k] = B(p.Panel);
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            r[k] = B(p.Hi);
        r["TextControlForegroundDisabled"] = B(p.Dim);
        r["TextControlBorderBrush"] = B(p.Frame);
        r["TextControlBorderBrushPointerOver"] = B(p.FrameHi);
        r["TextControlBorderBrushFocused"] = B(p.Accent);
        r["TextControlPlaceholderForeground"] = B(p.Dim);
        r["TextControlPlaceholderForegroundPointerOver"] = B(p.Dim);
        r["TextControlPlaceholderForegroundFocused"] = B(p.Dim);
        r["TextControlSelectionHighlightColor"] = B(p.SelBg);
        r["TextControlButtonForeground"] = B(p.Accent);
        r["TextControlButtonForegroundPointerOver"] = B(p.Hi);

        r["MenuFlyoutPresenterBackground"] = B(p.Panel);
        r["MenuFlyoutPresenterBorderBrush"] = B(p.FrameHi);
        r["MenuFlyoutItemBackground"] = Brushes.Transparent;
        r["MenuFlyoutItemBackgroundPointerOver"] = B(p.SelBg);
        r["MenuFlyoutItemBackgroundPressed"] = B(p.SelBg);
        r["MenuFlyoutItemForeground"] = B(p.Fg);
        r["MenuFlyoutItemForegroundPointerOver"] = B(p.SelFg);
        r["MenuFlyoutItemForegroundPressed"] = B(p.SelFg);
        r["MenuFlyoutItemForegroundDisabled"] = B(p.Dim);
        r["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = B(p.Dim);
        r["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = B(p.SelFg);
        r["MenuFlyoutSubItemChevron"] = B(p.Accent);
        r["MenuFlyoutSeparatorBackground"] = B(p.Frame);

        r["ToolTipBackground"] = B(p.Panel);
        r["ToolTipForeground"] = B(p.Hi);
        r["ToolTipBorderBrush"] = B(p.Accent);

        r["ScrollBarThumbFill"] = B(p.Frame);
        r["ScrollBarThumbFillPointerOver"] = B(p.FrameHi);
        r["ScrollBarThumbFillPressed"] = B(p.Accent);
        r["ScrollBarPanningThumbBackground"] = B(p.Frame);
        r["ScrollBarTrackFill"] = B(p.Panel);
        r["ScrollBarTrackFillPointerOver"] = B(p.Panel);
        r["ScrollBarButtonArrowForeground"] = B(p.Accent);

        r["RepeatButtonBackground"] = B(p.Panel);
        r["RepeatButtonForeground"] = B(p.Accent);
        r["RepeatButtonBackgroundPointerOver"] = B(p.SelBg);
        r["RepeatButtonForegroundPointerOver"] = B(p.SelFg);
        r["RepeatButtonBorderBrush"] = B(p.Frame);

        r["ControlCornerRadius"] = new CornerRadius(0);
        r["OverlayCornerRadius"] = new CornerRadius(0);
    }
}
