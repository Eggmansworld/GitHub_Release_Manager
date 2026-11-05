namespace EggmansReleaseManager.ViewModels;

public enum MsgKind { Info, Warn, Error, Ok }

/// <summary>Things the view-model needs from the window (dialogs, pickers).</summary>
public interface IUiHost
{
    Task ShowMessage(string title, string message, MsgKind kind = MsgKind.Info);
    Task<bool> Confirm(string title, string message);
    /// <summary>Returns the index of the option picked; the last option is used for Esc.</summary>
    Task<int> Choose(string title, string message, params string[] options);
    Task<string?> PickFolder(string title, string? start);
    Task<string?> PickSaveFile(string title, string suggestedName);
    Task<bool> ShowFilters(FilterEditorViewModel vm);
    Task<bool> ShowSettings(SettingsViewModel vm);
    Task ShowAbout();
    void ScrollLogToEnd();
}
