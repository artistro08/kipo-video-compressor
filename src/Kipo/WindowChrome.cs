using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kipo;

/// <summary>
/// Shared window setup for Kipo's windows.
/// </summary>
/// <remarks>
/// Draws the XAML title bar edge to edge, makes the minimize/maximize/close buttons the
/// full 48px title bar height, sets the Kipo icon for the taskbar and Alt+Tab, and keeps
/// those window buttons matching the app's light or dark theme.
/// </remarks>
public static class WindowChrome
{
    /// <summary>Applies the Kipo title bar, icon and theme-aware window buttons.</summary>
    /// <param name="window">Window to set up.</param>
    /// <param name="titleBar">The window's XAML title bar.</param>
    public static void Apply(Window window, TitleBar titleBar)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.ExtendsContentIntoTitleBar                 = true;
        window.SetTitleBar(titleBar);
        window.AppWindow.TitleBar.PreferredHeightOption   = TitleBarHeightOption.Tall;
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "kipo.ico"));

        if (window.Content is FrameworkElement page)
        {
            page.ActualThemeChanged += (_, _) => SyncButtons(window, page);
            page.Loaded             += (_, _) => SyncButtons(window, page);
        }
    }

    /// <summary>
    /// Opens an InfoBar and closes it again after <paramref name="duration"/>.
    /// </summary>
    /// <param name="bar">The banner.</param>
    /// <param name="duration">How long it stays.</param>
    public static void ShowBriefly(InfoBar bar, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(bar);

        bar.IsOpen = true;

        var timer = bar.DispatcherQueue.CreateTimer();
        timer.Interval    = duration;
        timer.IsRepeating = false;
        timer.Tick       += (_, _) => bar.IsOpen = false;
        timer.Start();
    }

    private static void SyncButtons(Window window, FrameworkElement page) =>
        window.AppWindow.TitleBar.PreferredTheme = page.ActualTheme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;
}
