using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Kipo;

/// <summary>
/// Keeps a window exactly as tall as its content, with an eased resize.
/// </summary>
/// <remarks>
/// Measures the page with unlimited height (star rows then act like Auto) to get its
/// natural height, and eases the window there over a quarter second. Dragging the window
/// taller than the content is blocked; dragging shorter is allowed down to the first
/// (smallest) height the window had. The window waits off-screen until its first fit, then
/// appears centered on the screen with the mouse, so it never shows at the wrong size.
/// Used by the main window and the compress dialog.
/// </remarks>
public sealed partial class WindowFitter
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);

    private DispatcherQueueTimer? _timer;
    private System.Diagnostics.Stopwatch? _clock;
    private Action? _done;
    private int _from;
    private int _to;
    private int _minOuterHeight;

    private readonly Window _window;
    private readonly Func<bool> _paused;
    private int _width;

    /// <summary>Parks the window off-screen until <see cref="FitFirst"/> sizes it.</summary>
    /// <param name="window">Window to size; not shown yet.</param>
    /// <param name="width">Starting window width in DIPs; change it with <see cref="SetWidth"/>.</param>
    /// <param name="paused">Returns true while sizing should wait (for example, settings open).</param>
    public WindowFitter(Window window, int width, Func<bool> paused)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _paused = paused;
        _width  = width;

        window.AppWindow.Move(new PointInt32(OffScreen, OffScreen));
    }

    private const int OffScreen = -32_000;

    private AppWindow AppWindow => _window.AppWindow;

    /// <summary>Changes the window width (DIPs) right away; height follows its content.</summary>
    /// <param name="dips">New width.</param>
    public void SetWidth(int dips)
    {
        _width    = dips;
        var scale = _window.Content.XamlRoot?.RasterizationScale ?? 1.0;

        AppWindow.ResizeClient(new SizeInt32((int)(_width * scale), AppWindow.ClientSize.Height));
        FitLater();
    }

    /// <summary>First show: sets the width, snaps the height, then brings the window on-screen.</summary>
    public void FitFirst()
    {
        var scale = _window.Content.XamlRoot?.RasterizationScale ?? 1.0;

        AppWindow.ResizeClient(new SizeInt32((int)(_width * scale), AppWindow.ClientSize.Height));
        _window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            Fit(animate: false);
            Center();
        });
    }

    // Centers the window on the work area of the screen with the mouse
    private void Center()
    {
        var mouse = GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : default;
        var area  = DisplayArea.GetFromPoint(mouse, DisplayAreaFallback.Primary).WorkArea;
        var size  = AppWindow.Size;

        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint point);

    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Resizes after the current layout change lands, so the measure sees it.</summary>
    public void FitLater() =>
        _window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Fit(animate: true));

    private void Fit(bool animate)
    {
        if (_paused() || _window.Content is not FrameworkElement page || page.XamlRoot is null)
        {
            return;
        }

        // Natural content height
        page.Measure(new Windows.Foundation.Size(page.ActualWidth, double.PositiveInfinity));

        var scale        = page.XamlRoot.RasterizationScale;
        var clientTarget = (int)Math.Ceiling(page.DesiredSize.Height * scale);
        var frame        = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var outerTarget  = clientTarget + frame;
        var presenter    = (OverlappedPresenter)AppWindow.Presenter;

        page.InvalidateMeasure();

        if (_minOuterHeight == 0)
        {
            _minOuterHeight = outerTarget;
        }

        presenter.PreferredMinimumHeight = Math.Min(_minOuterHeight, outerTarget);
        presenter.PreferredMaximumHeight = Math.Max(outerTarget, AppWindow.Size.Height);

        if (!animate)
        {
            AppWindow.ResizeClient(new SizeInt32(AppWindow.ClientSize.Width, clientTarget));
            presenter.PreferredMaximumHeight = outerTarget;
            return;
        }

        Animate(clientTarget, () => presenter.PreferredMaximumHeight = outerTarget);
    }

    private void Animate(int target, Action done)
    {
        _from  = AppWindow.ClientSize.Height;
        _to    = target;
        _done  = done;
        _clock = System.Diagnostics.Stopwatch.StartNew();

        if (_timer is null)
        {
            _timer             = _window.DispatcherQueue.CreateTimer();
            _timer.Interval    = TimeSpan.FromMilliseconds(15);
            _timer.IsRepeating = true;
            _timer.Tick       += OnTick;
        }

        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var progress = Math.Min(1, _clock!.Elapsed.TotalMilliseconds / Duration.TotalMilliseconds);
        var eased    = 1 - Math.Pow(1 - progress, 3);
        var height   = (int)Math.Round(_from + (_to - _from) * eased);

        AppWindow.ResizeClient(new SizeInt32(AppWindow.ClientSize.Width, height));

        if (progress >= 1)
        {
            sender.Stop();
            _done?.Invoke();
        }
    }
}
