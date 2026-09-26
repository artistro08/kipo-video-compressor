using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Kipo.Core;

/// <summary>Where a queued file is in its life.</summary>
public enum ItemStatus
{
    Waiting,
    Compressing,
    Done,
    DoneOverTarget,
    Skipped,
    Failed,
    Canceled,
}

/// <summary>
/// What a worker reports back for one file.
/// </summary>
/// <param name="Status">Final status.</param>
/// <param name="OutputPath">Output file, when one was written.</param>
/// <param name="Detail">Failure reason, or MB over target for <see cref="ItemStatus.DoneOverTarget"/>.</param>
public sealed record CompressResult(ItemStatus Status, string? OutputPath = null, string? Detail = null);

/// <summary>
/// One row in the queue.
/// </summary>
/// <remarks>
/// Raises <see cref="PropertyChanged"/> so the UI updates live. <see cref="StatusText"/> is
/// the exact text shown in the row. Each item owns its own cancellation source, so one
/// row can be canceled without touching the others.
/// </remarks>
public sealed class QueueItem : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private ItemStatus _status;
    private double _progress;
    private string? _detail;

    /// <summary>Creates a waiting item.</summary>
    /// <param name="path">Source file path.</param>
    /// <param name="targetBytes">Target size in bytes, fixed when the file was added.</param>
    public QueueItem(string path, long targetBytes)
    {
        Path        = path;
        TargetBytes = targetBytes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public long TargetBytes { get; }

    public string? OutputPath { get; set; }

    public CancellationToken Token => _cancel.Token;

    public ItemStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value, nameof(StatusText), nameof(CanCancel)))
            {
                Raise(nameof(IsDone));
                Raise(nameof(HasOutput));
            }
        }
    }

    /// <summary>Encode progress, 0–100.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value, nameof(StatusText));
    }

    public string? Detail
    {
        get => _detail;
        set => Set(ref _detail, value, nameof(StatusText));
    }

    public bool CanCancel => Status is ItemStatus.Waiting or ItemStatus.Compressing;

    /// <summary>True once the file needs nothing more: compressed, or already under target. The row shows a checkmark.</summary>
    public bool IsDone => Status is ItemStatus.Done or ItemStatus.DoneOverTarget or ItemStatus.Skipped;

    /// <summary>True when a compressed file was written; the row then offers Show file and Open in folder.</summary>
    public bool HasOutput => Status is ItemStatus.Done or ItemStatus.DoneOverTarget && OutputPath is not null;

    public string StatusText => Status switch
    {
        ItemStatus.Waiting        => "Waiting",
        ItemStatus.Compressing    => string.Create(CultureInfo.InvariantCulture, $"Compressing {Math.Round(Progress):0}%"),
        ItemStatus.Done           => "Done",
        ItemStatus.DoneOverTarget => $"Done (over target by {Detail} MB)",
        ItemStatus.Skipped        => "Already under target",
        ItemStatus.Failed         => $"Failed: {Detail}",
        ItemStatus.Canceled       => "Canceled",
        _                         => Status.ToString(),
    };

    /// <summary>Cancels this item. Waiting items flip to Canceled right away.</summary>
    public void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        if (Status == ItemStatus.Waiting)
        {
            Status = ItemStatus.Canceled;
        }

        _cancel.Cancel();
    }

    public void Dispose() => _cancel.Dispose();

    // Property name comes from the calling setter; returns true when the value changed
    private bool Set<T>(ref T field, T value, string first, string? second = null, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        Raise(first);

        if (second is not null)
        {
            Raise(second);
        }

        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
