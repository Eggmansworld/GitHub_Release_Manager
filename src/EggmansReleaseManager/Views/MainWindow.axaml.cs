using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EggmansReleaseManager.UI;
using EggmansReleaseManager.ViewModels;

namespace EggmansReleaseManager.Views;

public partial class MainWindow : Window, IUiHost
{
    private bool _scrollQueued;

    public MainWindow()
    {
        InitializeComponent();
        WindowChrome.Attach(this, this.FindControl<Control>("TitleDrag")!);

        var folderBox = this.FindControl<TextBox>("FolderBox")!;
        DragDrop.SetAllowDrop(folderBox, true);
        folderBox.AddHandler(DragDrop.DragOverEvent, OnFolderDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        folderBox.AddHandler(DragDrop.DropEvent, OnFolderDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel m) return;
            m.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.NoTransfers) or nameof(MainViewModel.HasRaces)) UpdateDccRows();
            };
            UpdateDccRows();
        };
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var (w, h) = vm.WindowSize;
                if (w >= MinWidth && h >= MinHeight) { Width = w; Height = h; }
                RestoreRepoColumns(vm.RepoColumnWidths);
                ScrollLogToEnd();
            }
        };
        Closing += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (WindowState == WindowState.Normal) vm.SaveWindowSize(Width, Height);
            SaveRepoColumns(vm.RepoColumnWidths);
            vm.HangUp();
        };
    }

    /// <summary>Transfers on top, race board below; whichever is empty gives its space to the other.</summary>
    private void UpdateDccRows()
    {
        if (DataContext is not MainViewModel vm) return;
        var rows = DccGrid.RowDefinitions;
        rows[0].Height = !vm.NoTransfers || !vm.HasRaces ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        rows[2].Height = vm.HasRaces ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    // ───────── Tracked Repos column widths (RepoGrid field is generated from x:Name) ─────────

    private void RestoreRepoColumns(Dictionary<string, double> widths)
    {
        foreach (var col in RepoGrid.Columns)
        {
            if (col.Tag is not string id || !col.CanUserResize || col.Width.IsStar) continue;
            if (widths.TryGetValue(id, out var w) && w >= col.MinWidth && w < 2000)
                col.Width = new DataGridLength(w);
        }
    }

    private void SaveRepoColumns(Dictionary<string, double> widths)
    {
        foreach (var col in RepoGrid.Columns)
        {
            if (col.Tag is not string id || !col.CanUserResize || col.Width.IsStar) continue;
            if (col.ActualWidth > 0) widths[id] = Math.Round(col.ActualWidth);
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ───────── chrome ─────────
    private void OnMinimize(object? s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object? s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? s, RoutedEventArgs e) => Close();

    // ───────── repo list / folder box ─────────
    private void OnRepoDoubleTapped(object? sender, TappedEventArgs e)
    {
        // only rows open the folder - double-clicking a header/resize grip must not
        if (e.Source is Visual v && v.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null)
            Vm?.OpenRepoFolderCommand.Execute(null);
    }

    private void OnFolderLostFocus(object? sender, RoutedEventArgs e) => Vm?.CommitRootFolder();

    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // IDataObject API is fine for this simple case
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
#pragma warning restore CS0618
        e.Handled = true;
    }

    private void OnFolderDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var item = e.Data.GetFiles()?.FirstOrDefault();
#pragma warning restore CS0618
        var path = item?.TryGetLocalPath();
        if (path is not null) Vm?.DropFolder(path);
        e.Handled = true;
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || Clipboard is null) return;
        var text = string.Join(Environment.NewLine, Vm.Log.Select(l => $"{l.Time} {l.Prefix} {l.Text}"));
        await Clipboard.SetTextAsync(text);
    }

    // ───────── IUiHost ─────────
    public void ScrollLogToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            var list = this.FindControl<ListBox>("LogList");
            if (list?.ItemCount > 0) list.ScrollIntoView(list.ItemCount - 1);
        }, DispatcherPriority.Background);
    }

    public Task ShowMessage(string title, string message, MsgKind kind = MsgKind.Info) =>
        RetroDialog.Show(this, title, message, kind, confirm: false);

    public Task<bool> Confirm(string title, string message) =>
        RetroDialog.Show(this, title, message, MsgKind.Warn, confirm: true);

    public Task<int> Choose(string title, string message, params string[] options) =>
        RetroDialog.Choose(this, title, message, MsgKind.Warn, options, options.Length - 1);

    public Task ShowAbout() => RetroDialog.ShowNfo(this);

    public Task<bool> ShowRules() => RetroDialog.ShowRules(this);

    public Task ShowTopBoard(string text) => RetroDialog.ShowTopBoard(this, text);

    public async Task<string?> PickFolder(string title, string? start)
    {
        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            startFolder = await StorageProvider.TryGetFolderFromPathAsync(start);
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFile(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON files") { Patterns = ["*.json"] }, FilePickerFileTypes.All],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ShowFilters(FilterEditorViewModel vm)
    {
        var w = new FilterWindow { DataContext = vm };
        return await w.ShowDialog<bool>(this);
    }

    public async Task<bool> ShowSettings(SettingsViewModel vm)
    {
        var w = new SettingsWindow { DataContext = vm };
        return await w.ShowDialog<bool>(this);
    }
}
