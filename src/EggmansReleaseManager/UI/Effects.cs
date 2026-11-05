using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EggmansReleaseManager.UI;

/// <summary>CRT scanline overlay: a dark line every 3px. Purely decorative, never hit-testable.</summary>
public sealed class ScanlineOverlay : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<ScanlineOverlay, IBrush?>(nameof(LineBrush));

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    static ScanlineOverlay()
    {
        AffectsRender<ScanlineOverlay>(LineBrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ScanlineOverlay>(false);
    }

    public override void Render(DrawingContext context)
    {
        var brush = LineBrush;
        if (brush is null) return;
        var w = Bounds.Width;
        for (double y = 1; y < Bounds.Height; y += 3)
            context.FillRectangle(brush, new Rect(0, y, w, 1));
    }
}

/// <summary>Cracktro-style horizontal text scroller.</summary>
public sealed class Marquee : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<Marquee, string>(nameof(Text), "");

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<Marquee, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> Foreground2Property =
        AvaloniaProperty.Register<Marquee, IBrush?>(nameof(Foreground2));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<Marquee>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<Marquee>();

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Foreground2 { get => GetValue(Foreground2Property); set => SetValue(Foreground2Property, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private readonly DispatcherTimer _timer;
    private double _offset;
    private FormattedText? _ft;
    private DateTime _last = DateTime.UtcNow;

    static Marquee()
    {
        AffectsMeasure<Marquee>(TextProperty, FontFamilyProperty, FontSizeProperty);
        ClipToBoundsProperty.OverrideDefaultValue<Marquee>(true);
    }

    public Marquee()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _last = DateTime.UtcNow;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontFamilyProperty ||
            change.Property == FontSizeProperty || change.Property == ForegroundProperty)
            _ft = null;
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible) _timer.Start(); else _timer.Stop();
        }
    }

    private FormattedText Ft() => _ft ??= new FormattedText(Text ?? "", System.Globalization.CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize, Foreground ?? Brushes.White);

    protected override Size MeasureOverride(Size availableSize) => new(0, Ft().Height + 2);

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var dt = (now - _last).TotalSeconds;
        _last = now;
        _offset += dt * 70; // px per second
        var w = Ft().Width;
        if (_offset > w + Bounds.Width) _offset = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var ft = Ft();
        var x = Bounds.Width - _offset;
        // gentle sine wobble, because demoscene
        var y = (Bounds.Height - ft.Height) / 2 + Math.Sin(_offset / 40.0) * 1.5;
        context.DrawText(ft, new Point(x, y));
    }
}
