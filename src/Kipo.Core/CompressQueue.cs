using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Kipo.Core;

/// <summary>Does the work for one queued file.</summary>
/// <param name="item">The file.</param>
/// <param name="progress">Receives 0–100.</param>
/// <param name="cancellationToken">Canceled when the user cancels the row.</param>
/// <returns>The outcome.</returns>
public delegate Task<CompressResult> CompressWorker(QueueItem item, IProgress<double> progress, CancellationToken cancellationToken);

/// <summary>
/// Runs files one at a time, in the order added.
/// </summary>
/// <remarks>
/// Adding files starts the queue if it's idle; files added while it runs join the end.
/// A "batch" is everything added between idle and idle; <see cref="BatchCompleted"/>
/// fires once per batch with all its items. Create and use the queue from the UI thread:
/// awaits resume there, so <see cref="Items"/> and item properties change on the UI thread.
/// </remarks>
/// <param name="worker">Does the actual compression.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is a work queue; the name says what it does.")]
public sealed class CompressQueue(CompressWorker worker) : INotifyPropertyChanged
{
    private readonly List<QueueItem> _batch = [];
    private Task _running = Task.CompletedTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while any row is waiting or compressing; drives the Cancel all link.</summary>
    public bool CanCancelAny => Items.Any(item => item.CanCancel);

    /// <summary>Fires when the queue goes idle, with every item added since it last went idle.</summary>
    public event EventHandler<IReadOnlyList<QueueItem>>? BatchCompleted;

    /// <summary>All rows ever added, oldest first.</summary>
    public ObservableCollection<QueueItem> Items { get; } = [];

    /// <summary>
    /// Validates and queues paths. Invalid ones get a Failed row and never run.
    /// </summary>
    /// <param name="paths">Candidate file paths.</param>
    /// <param name="targetBytes">Target size for these files.</param>
    public void AddPaths(IEnumerable<string> paths, long targetBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var path in paths)
        {
            var item  = new QueueItem(path, targetBytes);
            var error = PathValidator.Validate(path);

            if (error is not null)
            {
                item.Detail = error;
                item.Status = ItemStatus.Failed;
            }

            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
            _batch.Add(item);
        }

        RaiseCanCancelAny();

        if (_running.IsCompleted && _batch.Count > 0)
        {
            _running = RunAsync();
        }
    }

    /// <summary>Cancels every waiting and running item.</summary>
    public void CancelAll()
    {
        foreach (var item in Items)
        {
            item.Cancel();
        }
    }

    /// <summary>Completes when the queue has nothing left to run.</summary>
    /// <returns>The current run, or a completed task.</returns>
    public Task WhenIdle() => _running;

    /// <summary>
    /// Decides whether a finished batch's result goes on the clipboard.
    /// </summary>
    /// <param name="batch">Items from <see cref="BatchCompleted"/>.</param>
    /// <param name="enabled">The user's clipboard setting.</param>
    /// <returns>Output path to copy, or null.</returns>
    public static string? ShouldCopyToClipboard(IReadOnlyList<QueueItem> batch, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!enabled || batch.Count != 1)
        {
            return null;
        }

        var item = batch[0];

        return item.Status is ItemStatus.Done or ItemStatus.DoneOverTarget ? item.OutputPath : null;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueItem.CanCancel))
        {
            RaiseCanCancelAny();
        }
    }

    private void RaiseCanCancelAny() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancelAny)));

    private async Task RunAsync()
    {
        while (Items.FirstOrDefault(i => i.Status == ItemStatus.Waiting) is { } item)
        {
            await RunOneAsync(item);
        }

        var finished = _batch.ToList();
        _batch.Clear();
        BatchCompleted?.Invoke(this, finished);
    }

    private async Task RunOneAsync(QueueItem item)
    {
        item.Status   = ItemStatus.Compressing;
        item.Progress = 0;

        var progress = new Progress<double>(value => item.Progress = value);

        try
        {
            var result = await worker(item, progress, item.Token);

            item.OutputPath = result.OutputPath;
            item.Detail     = result.Detail;
            item.Status     = result.Status;
        }
        catch (OperationCanceledException)
        {
            item.Status = ItemStatus.Canceled;
        }
#pragma warning disable CA1031 // Any worker failure becomes a Failed row instead of crashing the app
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error($"Compressing {item.Path} failed", ex);
            item.Detail = ex.Message;
            item.Status = ItemStatus.Failed;
        }
    }
}
