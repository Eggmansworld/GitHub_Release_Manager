using CommunityToolkit.Mvvm.ComponentModel;
using EggmansReleaseManager.Models;
using EggmansReleaseManager.UI;

namespace EggmansReleaseManager.ViewModels;

public sealed partial class ThemeChoice : ObservableObject
{
    public required Palette Palette { get; init; }
    public string Name => Palette.Name;
    public string Blurb => Palette.Blurb;
    [ObservableProperty] private bool _isSelected;
    public Action<ThemeChoice>? Selected { get; set; }
    partial void OnIsSelectedChanged(bool value) { if (value) Selected?.Invoke(this); }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private decimal _maxThreads;
    [ObservableProperty] private decimal _autoUpdateInterval;
    [ObservableProperty] private bool _autoUpdateEnabled;
    [ObservableProperty] private string _discordWebhook;
    [ObservableProperty] private string _githubToken;
    [ObservableProperty] private bool _revealToken;
    [ObservableProperty] private string _fontFamily;
    [ObservableProperty] private decimal _fontSize;
    [ObservableProperty] private bool _crtScanlines;
    [ObservableProperty] private bool _scroller;
    [ObservableProperty] private bool _modemIntro;

    public List<ThemeChoice> Themes { get; }
    public string SelectedThemeId { get; private set; }

    public SettingsViewModel(AppConfig c)
    {
        _maxThreads = c.MaxThreads;
        _autoUpdateInterval = c.AutoUpdateInterval;
        _autoUpdateEnabled = c.AutoUpdateEnabled;
        _discordWebhook = c.DiscordWebhook;
        _githubToken = c.GithubToken;
        _fontFamily = c.FontFamily;
        _fontSize = (decimal)c.FontSize;
        _crtScanlines = c.CrtScanlines;
        _scroller = c.Scroller;
        _modemIntro = c.ModemIntro;
        SelectedThemeId = ThemeManager.Get(c.Theme).Id;

        Themes = ThemeManager.Palettes.Select(p => new ThemeChoice
        {
            Palette = p,
            IsSelected = p.Id == SelectedThemeId,
        }).ToList();
        foreach (var t in Themes) t.Selected = OnThemePicked;
    }

    private void OnThemePicked(ThemeChoice choice)
    {
        SelectedThemeId = choice.Palette.Id;
        Preview();
    }

    partial void OnFontFamilyChanged(string value) => Preview();
    partial void OnFontSizeChanged(decimal value) => Preview();

    /// <summary>Live preview while the dialog is open (reverted on cancel).</summary>
    private void Preview() => ThemeManager.Apply(SelectedThemeId, FontFamily, (double)FontSize);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private static void TestDial() => Services.ModemSound.Play(Services.ModemSound.Build().Wav);

    public char TokenChar => RevealToken ? '\0' : '•';
    partial void OnRevealTokenChanged(bool value) => OnPropertyChanged(nameof(TokenChar));

    public void ApplyTo(AppConfig c)
    {
        c.MaxThreads = (int)Math.Clamp(MaxThreads, 1, 16);
        c.AutoUpdateInterval = (int)Math.Clamp(AutoUpdateInterval, 5, 1440);
        c.AutoUpdateEnabled = AutoUpdateEnabled;
        c.DiscordWebhook = DiscordWebhook.Trim();
        c.GithubToken = GithubToken.Trim();
        c.Theme = SelectedThemeId;
        c.FontFamily = FontFamily.Trim();
        c.FontSize = (double)Math.Clamp(FontSize, 9, 24);
        c.CrtScanlines = CrtScanlines;
        c.Scroller = Scroller;
        c.ModemIntro = ModemIntro;
    }
}
