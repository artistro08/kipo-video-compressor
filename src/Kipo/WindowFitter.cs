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
/// (smallest) height the window had. Used by the main window and the compress dialog.
/// </remarks>
/// <param name="window">Window to size.</param>
/// <param name="width">Window width in DIPs.</param>
/// <param name="paused">Returns true while sizing should wait (for example, settings open).</param>
public sealed class WindowFitter(Window window, int width, Func<bool> paused)
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);

    private DispatcherQueueTimer? _timer;
    private System.Diagnostics.Stopwatch? _clock;
    private Action? _done;
    private int _from;
    private int _to;
    private int _minOuterHeight;

    private AppWindow AppWindow => window.AppWindow;

    /// <summary>First show: sets the width and snaps the height without animating.</summary>
    public void FitFirst()
    {
        var scale = window.Content.XamlRoot?.RasterizationScale ?? 1.0;

        AppWindow.ResizeClient(new SizeInt32((int)(width * scale), AppWindow.ClientSize.Height));
        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Fit(animate: false));
    }

    /// <summary>Resizes after the current layout change lands, so the measure sees it.</summary>
    public void FitLater() =>
        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Fit(animate: true));

    private void Fit(bool animate)
    {
        if (paused() || window.Content is not FrameworkElement page || page.XamlRoot is null)
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
            _timer             = window.DispatcherQueue.CreateTimer();
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
