namespace Kipo.Core;

/// <summary>
/// Groups right-click launches that arrive close together into one dialog.
/// </summary>
/// <remarks>
/// The classic right-click menu starts Kipo once per selected file, so a 3-file selection
/// arrives as 3 requests a few milliseconds apart. A request joins the last dialog when it
/// asks for the same size (or Custom) and arrives within <c>window</c> of the previous
/// request; otherwise a new dialog opens. Use from the UI thread.
/// </remarks>
/// <typeparam name="TTarget">The dialog type.</typeparam>
/// <param name="now">Clock; tests pass a fake.</param>
/// <param name="window">How close requests must be to share a dialog.</param>
/// <param name="open">Opens a new dialog for a request.</param>
/// <param name="add">Adds paths to an existing dialog.</param>
public sealed class RequestBatcher<TTarget>(
    Func<DateTimeOffset> now,
    TimeSpan window,
    Func<LaunchRequest, TTarget> open,
    Action<TTarget, IReadOnlyList<string>> add)
{
    private TTarget? _last;
    private (int? SizeMb, bool Custom) _lastSize;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Sends a compress request to a new or the most recent dialog.
    /// </summary>
    /// <param name="request">A <see cref="LaunchAction.Compress"/> request.</param>
    /// <exception cref="ArgumentException">When the request isn't a compress request.</exception>
    public void Submit(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Action != LaunchAction.Compress)
        {
            throw new ArgumentException("Only compress requests can be batched.", nameof(request));
        }

        var at   = now();
        var size = (request.SizeMb, request.Custom);

        if (_last is not null && size == _lastSize && at - _lastAt <= window)
        {
            add(_last, request.Paths);
        }
        else
        {
            _last     = open(request);
            _lastSize = size;
        }

        _lastAt = at;
    }

    /// <summary>
    /// Stops sending files to a dialog, for example because the user closed it.
    /// </summary>
    /// <param name="target">The dialog that closed.</param>
    public void Forget(TTarget target)
    {
        if (_last is not null && ReferenceEquals(_last, target))
        {
            _last = default;
        }
    }
}
