using Avalonia.Controls;
using Avalonia.Input;

namespace EggmansReleaseManager.Views;

/// <summary>Makes a control act as the title bar of a chrome-less window (drag to move, double-click to maximize).</summary>
public static class WindowChrome
{
    public static void Attach(Window window, Control dragArea, bool allowMaximize = true)
    {
        dragArea.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(dragArea).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && allowMaximize && window.CanResize)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            window.BeginMoveDrag(e);
        };
    }
}
