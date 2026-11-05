using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EggmansReleaseManager.Views;

public partial class FilterWindow : Window
{
    public FilterWindow()
    {
        InitializeComponent();
        WindowChrome.Attach(this, this.FindControl<Control>("TitleDrag")!);
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
