using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EggmansReleaseManager.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowChrome.Attach(this, this.FindControl<Control>("TitleDrag")!, allowMaximize: false);
        // never taller than the screen; the panels scroll instead
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } scr)
                MaxHeight = scr.WorkingArea.Height / scr.Scaling - 24;
        };
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
