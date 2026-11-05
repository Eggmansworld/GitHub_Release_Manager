using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EggmansReleaseManager.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowChrome.Attach(this, this.FindControl<Control>("TitleDrag")!, allowMaximize: false);
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
