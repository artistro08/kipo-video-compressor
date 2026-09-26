using System.Globalization;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Kipo.Core;

/// <summary>Reasons an encode fails. Shown after "Failed: " in the queue.</summary>
public static class CompressErrors
{
    public const string CantWrite  = "can't write output";
    public const string CantEncode = "can't encode this video";
}

/// <summary>
/// Compresses one video to a target size with the built-in Windows MediaTranscoder.
/// </summary>
/// <remarks>
/// Reads the source's size, length, frame rate and audio, asks <see cref="EncodePlanner"/>
/// for a plan, then encodes H.264 + AAC into <c>name-compressed.part</c> next to the source.
/// If the result is over target it retries once at a lower bitrate. On success the
/// <c>.part</c> is renamed to the final name; on failure or cancel it's deleted. The
/// source is only ever read.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/uwp/api/windows.media.transcoding.mediatranscoder"/>
public static class Compressor
{
    /// <summary>
    /// Compresses a file.
    /// </summary>
    /// <param name="sourcePath">Validated source path.</param>
    /// <param name="targetBytes">Target output size.</param>
    /// <param name="progress">Receives 0–100 per encode pass.</param>
    /// <param name="cancellationToken">Stops the encode.</param>
    /// <returns>The outcome. Expected failures come back as <see cref="ItemStatus.Failed"/>.</returns>
    /// <exception cref="OperationCanceledException">When canceled.</exception>
    public static async Task<CompressResult> CompressAsync(string sourcePath, long targetBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var partPath = OutputNamer.GetPartPath(sourcePath);

        try
        {
            if (!File.Exists(sourcePath))
            {
                return Failed(InputErrors.NotFound);
            }

            // Plan
            var source = await StorageFile.GetFileFromPathAsync(sourcePath);
            var info   = await ReadInfoAsync(source);

            if (info is null)
            {
                return Failed(PlanErrors.CantRead);
            }

            var plan = EncodePlanner.Plan(info, new FileInfo(sourcePath).Length, targetBytes);

            if (plan.Kind == PlanKind.Skip)
            {
                return new CompressResult(ItemStatus.Skipped);
            }

            if (plan.Kind == PlanKind.Fail)
            {
                return Failed(plan.Error);
            }

            // Encode, with one retry if over target
            var folder       = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(sourcePath));
            var videoBitrate = plan.VideoBitrate;
            long actualBytes = 0;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await TranscodeAsync(source, folder, Path.GetFileName(partPath), plan with { VideoBitrate = videoBitrate }, info, progress, cancellationToken);

                actualBytes = new FileInfo(partPath).Length;

                if (actualBytes <= targetBytes)
                {
                    break;
                }

                videoBitrate = EncodePlanner.RetryBitrate(videoBitrate, targetBytes, actualBytes);
            }

            // Finish
            var outputPath = OutputNamer.GetOutputPath(sourcePath, File.Exists);
            File.Move(partPath, outputPath);

            if (actualBytes <= targetBytes)
            {
                return new CompressResult(ItemStatus.Done, outputPath);
            }

            var overMb = ((actualBytes - targetBytes) / (double)AppSettings.BytesPerMb).ToString("0.0", CultureInfo.InvariantCulture);

            return new CompressResult(ItemStatus.DoneOverTarget, outputPath, overMb);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(partPath);
            throw;
        }
        catch (FileNotFoundException)
        {
            DeleteQuietly(partPath);
            return Failed(InputErrors.NotFound);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error($"No write access for {sourcePath}", ex);
            DeleteQuietly(partPath);
            return Failed(CompressErrors.CantWrite);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException || ex.HResult < 0)
        {
            Log.Error($"Encode failed for {sourcePath}", ex);
            DeleteQuietly(partPath);
            return Failed(CompressErrors.CantEncode);
        }
    }

    /// <summary>
    /// Reads what the planner needs from a video file.
    /// </summary>
    /// <param name="file">Source file.</param>
    /// <returns>Video facts, or null when the file has no readable video track.</returns>
    public static async Task<VideoInfo?> ReadInfoAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);

        if (profile.Video is null)
        {
            return null;
        }

        var properties = await file.Properties.GetVideoPropertiesAsync();
        var frameRate  = profile.Video.FrameRate.Denominator == 0
            ? 0
            : profile.Video.FrameRate.Numerator / (double)profile.Video.FrameRate.Denominator;

        return new VideoInfo(properties.Duration, (int)profile.Video.Width, (int)profile.Video.Height, frameRate, profile.Audio is not null);
    }

    private static async Task TranscodeAsync(StorageFile source, StorageFolder folder, string partName, EncodePlan plan, VideoInfo info, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var part    = await folder.CreateFileAsync(partName, CreationCollisionOption.ReplaceExisting);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

        // Video
        profile.Video.Width   = (uint)plan.Width;
        profile.Video.Height  = (uint)plan.Height;
        profile.Video.Bitrate = plan.VideoBitrate;

        if (info.FrameRate > 0)
        {
            profile.Video.FrameRate.Numerator   = (uint)Math.Round(info.FrameRate * 1000);
            profile.Video.FrameRate.Denominator = 1000;
        }

        // Audio
        profile.Audio = info.HasAudio ? AudioEncodingProperties.CreateAac(48_000, 2, plan.AudioBitrate) : null;

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared   = await transcoder.PrepareFileTranscodeAsync(source, part, profile);

        if (!prepared.CanTranscode)
        {
            throw new InvalidOperationException($"Transcode not possible: {prepared.FailureReason}");
        }

        // Forward straight to the caller's progress so values stay on its thread and in order
        await prepared.TranscodeAsync().AsTask(cancellationToken, progress ?? new Progress<double>());
    }

    private static CompressResult Failed(string? reason) => new(ItemStatus.Failed, null, reason);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't delete {path}", ex);
        }
    }
}
