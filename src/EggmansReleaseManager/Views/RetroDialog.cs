using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EggmansReleaseManager.UI;
using EggmansReleaseManager.ViewModels;

namespace EggmansReleaseManager.Views;

/// <summary>ANSI-styled replacement for message boxes, built in code (no XAML needed).</summary>
public sealed class RetroDialog : Window
{
    private RetroDialog(string title, Control body, IEnumerable<(string Text, int Result, string? Class)> buttons, double maxWidth = 720)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        MaxWidth = maxWidth;

        var titleBar = new Border
        {
            Background = ThemeManager.Brush("SelBgBrush"),
            Padding = new Thickness(10, 3),
            Child = new TextBlock
            {
                Text = $"▓▒░ {title} ░▒▓",
                Foreground = ThemeManager.Brush("SelFgBrush"),
                FontWeight = FontWeight.Bold,
            },
        };
        WindowChrome.Attach(this, titleBar, allowMaximize: false);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
        };
        Button? first = null;
        foreach (var (text, result, cls) in buttons)
        {
            var b = new Button { Content = text, MinWidth = 90 };
            if (cls is not null) b.Classes.Add(cls);
            b.Click += (_, _) => Close(result);
            buttonRow.Children.Add(b);
            first ??= b;
        }

        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        panel.Children.Add(body);
        panel.Children.Add(buttonRow);

        Content = new Border
        {
            BorderBrush = ThemeManager.Brush("FrameHiBrush"),
            BorderThickness = new Thickness(1),
            Child = new Border
            {
                Margin = new Thickness(2),
                BorderBrush = ThemeManager.Brush("FrameBrush"),
                BorderThickness = new Thickness(1),
                Background = ThemeManager.Brush("PanelBrush"),
                Child = new DockPanel { Children = { WithDock(titleBar, Dock.Top), panel } },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(-1);
        };
        Opened += (_, _) => first?.Focus();
    }

    private static Control WithDock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }

    public static async Task<bool> Show(Window owner, string title, string message, MsgKind kind, bool confirm)
    {
        var options = confirm ? new[] { "YES", "NO" } : new[] { "OK" };
        return await Choose(owner, title, message, kind, options, confirm ? 1 : 0) == 0;
    }

    /// <summary>Message with any number of buttons; returns the index clicked, or -1 for Esc.</summary>
    public static async Task<int> Choose(Window owner, string title, string message, MsgKind kind, string[] options, int cancelIndex = -1)
    {
        var confirm = options.Length > 1;
        var (glyph, brushKey) = kind switch
        {
            MsgKind.Error => ("[×]", "ErrBrush"),
            MsgKind.Warn => (confirm ? "[?]" : "[!]", "WarnBrush"),
            MsgKind.Ok => ("[✓]", "OkBrush"),
            _ => ("[i]", "AccentBrush"),
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinWidth = 340 };
        var icon = new TextBlock
        {
            Text = glyph,
            Foreground = ThemeManager.Brush(brushKey),
            FontWeight = FontWeight.Bold,
            FontSize = 18,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var text = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeManager.Brush("HiBrush"),
            MaxWidth = 620,
        };
        Grid.SetColumn(text, 1);
        body.Children.Add(icon);
        body.Children.Add(text);

        var buttons = options.Select((o, i) => (o, i, i == 0 ? "hot" : (string?)null));
        var dlg = new RetroDialog(title, body, buttons);
        var r = await dlg.ShowDialog<int?>(owner);
        return r is null or -1 ? cancelIndex : r.Value;
    }

    public static async Task ShowNfo(Window owner)
    {
        var text = new SelectableTextBlock
        {
            Text = AsciiArt.Logo + "\n\n" + AsciiArt.Nfo,
            Foreground = ThemeManager.Brush("AccentBrush"),
            FontFamily = (FontFamily)Application.Current!.Resources["MonoFont"]!,
        };
        var dlg = new RetroDialog("EGGMAN.NFO", new ScrollViewer { Content = text, MaxHeight = 640 },
            [("CLOSE", 0, "hot")], maxWidth: 900);
        await dlg.ShowDialog<int?>(owner);
    }
}
