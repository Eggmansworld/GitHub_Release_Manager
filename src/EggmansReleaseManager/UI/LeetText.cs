using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using EggmansReleaseManager.Services;

namespace EggmansReleaseManager.UI;

/// <summary>
/// 31337 5P34K mode: every TextBlock / Run on screen goes through <see cref="Leet.Apply"/>.
/// Bindings stay intact (SetCurrentValue), the original text is remembered so switching it off restores it,
/// and text boxes are never touched. Mark a control (or an ancestor) with Classes="noleet" to exempt it.
/// </summary>
public static class LeetText
{
    private static readonly ConditionalWeakTable<AvaloniaObject, string> Originals = new();
    private static readonly List<WeakReference<Window>> Windows = [];
    private static bool _enabled, _busy, _installed;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (_installed) Sweep();
        }
    }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) => OnChanged(tb, TextBlock.TextProperty, e.NewValue as string));
        Run.TextProperty.Changed.AddClassHandler<Run>((run, e) => OnChanged(run, Run.TextProperty, e.NewValue as string));
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
        {
            Windows.RemoveAll(r => !r.TryGetTarget(out Window? _));
            Windows.Add(new WeakReference<Window>(w));
        });
    }

    private static void OnChanged(AvaloniaObject o, AvaloniaProperty<string?> prop, string? value)
    {
        if (_busy || !_enabled || string.IsNullOrEmpty(value) || Exempt(o)) return;
        var leet = Leet.Apply(value);
        if (leet == value) return;
        Originals.AddOrUpdate(o, value);
        Set(o, prop, leet);
    }

    private static void Set(AvaloniaObject o, AvaloniaProperty<string?> prop, string text)
    {
        _busy = true;
        try { o.SetCurrentValue(prop, text); }
        finally { _busy = false; }
    }

    private static bool Exempt(AvaloniaObject o)
    {
        for (var e = o as StyledElement; e is not null; e = e.Parent)
            if (e.Classes.Contains("noleet") || e is TextBox) return true;
        return false;
    }

    /// <summary>Re-applies (or undoes) the translation on everything currently open.</summary>
    private static void Sweep()
    {
        if (!_enabled)
        {
            foreach (var (o, original) in Originals.ToList())
                Set(o, o is Run ? Run.TextProperty : TextBlock.TextProperty, original);
            Originals.Clear();
            return;
        }
        var open = Windows.Select(r => r.TryGetTarget(out var w) ? w : null).Where(w => w is { IsVisible: true }).Select(w => w!).ToList();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            open = open.Union(d.Windows).ToList();
        foreach (var w in open)
            foreach (var tb in w.GetVisualDescendants().OfType<TextBlock>().ToList())
            {
                if (tb.Inlines is { Count: > 0 } inlines)
                    foreach (var run in inlines.OfType<Run>()) OnChanged(run, Run.TextProperty, run.Text);
                else OnChanged(tb, TextBlock.TextProperty, tb.Text);
            }
    }
}
