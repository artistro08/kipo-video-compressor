namespace Kipo.Core;

/// <summary>What Kipo knows about a source video before encoding.</summary>
/// <param name="Duration">Play length.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="FrameRate">Frames per second. Zero or less means unknown.</param>
/// <param name="HasAudio">True when the source has an audio track.</param>
public sealed record VideoInfo(TimeSpan Duration, int Width, int Height, double FrameRate, bool HasAudio);

/// <summary>What to do with a source file.</summary>
public enum PlanKind
{
    Encode,
    Skip,
    Fail,
}

/// <summary>Reasons a plan fails. Shown after "Failed: " in the queue.</summary>
public static class PlanErrors
{
    public const string TooSmall      = "target too small for this video length";
    public const string CantRead      = "can't read video";
    public const string InvalidTarget = "invalid target size";
}

/// <summary>
/// The encode settings for one file, or the reason there's nothing to encode.
/// </summary>
/// <param name="Kind">Encode, skip, or fail.</param>
/// <param name="Width">Output width (even). Zero unless encoding.</param>
/// <param name="Height">Output height (even). Zero unless encoding.</param>
/// <param name="VideoBitrate">Video bits per second.</param>
/// <param name="AudioBitrate">Audio bits per second. Zero when the source has no audio.</param>
/// <param name="Error">Failure reason when <paramref name="Kind"/> is <see cref="PlanKind.Fail"/>.</param>
public sealed record EncodePlan(PlanKind Kind, int Width, int Height, uint VideoBitrate, uint AudioBitrate, string? Error)
{
    /// <summary>Source is already under the target.</summary>
    public static EncodePlan Skip { get; } = new(PlanKind.Skip, 0, 0, 0, 0, null);

    /// <summary>Creates a failed plan.</summary>
    /// <param name="error">A <see cref="PlanErrors"/> value.</param>
    /// <returns>The plan.</returns>
    public static EncodePlan Failed(string error) => new(PlanKind.Fail, 0, 0, 0, 0, error);
}

/// <summary>
/// Works out resolution and bitrates that fit a video into a target size.
/// </summary>
/// <remarks>
/// Budget is target bytes × 8 × 0.95, leaving 5% for MP4 overhead. Audio gets 128 kbps,
/// or 64 kbps when that would starve the video. The rest goes to video. Resolution
/// steps down through 1080p, 720p, 480p and 360p (measured on the short side, so
/// portrait works) until bits-per-pixel reaches <see cref="TierBitsPerPixel"/>. Never
/// upscales. If 360p is still under the tier but above <see cref="MinBitsPerPixel"/>
/// it encodes there anyway; below that it fails.
/// </remarks>
public static class EncodePlanner
{
    public const double BudgetFactor     = 0.95;
    public const uint   AudioBitrateHigh = 128_000;
    public const uint   AudioBitrateLow  = 64_000;
    public const double TierBitsPerPixel = 0.04;
    public const double MinBitsPerPixel  = 0.015;
    public const double DefaultFrameRate = 30;

    private static readonly int[] Tiers = [1080, 720, 480, 360];

    /// <summary>
    /// Plans the encode for one file.
    /// </summary>
    /// <param name="info">Source video facts.</param>
    /// <param name="sourceBytes">Source file size.</param>
    /// <param name="targetBytes">Target output size.</param>
    /// <returns>The plan.</returns>
    public static EncodePlan Plan(VideoInfo info, long sourceBytes, long targetBytes)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (targetBytes <= 0)
        {
            return EncodePlan.Failed(PlanErrors.InvalidTarget);
        }

        if (sourceBytes <= targetBytes)
        {
            return EncodePlan.Skip;
        }

        if (info.Duration <= TimeSpan.Zero || info.Width <= 0 || info.Height <= 0)
        {
            return EncodePlan.Failed(PlanErrors.CantRead);
        }

        // Budget
        var frameRate     = info.FrameRate > 0 ? info.FrameRate : DefaultFrameRate;
        var bitsPerSecond = targetBytes * 8.0 * BudgetFactor / info.Duration.TotalSeconds;
        var sizes         = CandidateSizes(info.Width, info.Height);
        var smallest      = sizes[^1];

        // Audio
        var audio = info.HasAudio ? AudioBitrateHigh : 0;
        var video = bitsPerSecond - audio;

        if (info.HasAudio && BitsPerPixel(video, smallest, frameRate) < TierBitsPerPixel)
        {
            audio = AudioBitrateLow;
            video = bitsPerSecond - audio;
        }

        // Resolution
        foreach (var size in sizes)
        {
            if (BitsPerPixel(video, size, frameRate) >= TierBitsPerPixel)
            {
                return Encode(size, video, audio);
            }
        }

        return BitsPerPixel(video, smallest, frameRate) >= MinBitsPerPixel
            ? Encode(smallest, video, audio)
            : EncodePlan.Failed(PlanErrors.TooSmall);
    }

    /// <summary>
    /// Scales a frame so its short side is at most <paramref name="shortSide"/>, keeping aspect ratio and even sizes.
    /// </summary>
    /// <param name="width">Source width.</param>
    /// <param name="height">Source height.</param>
    /// <param name="shortSide">Largest allowed short side.</param>
    /// <returns>Even width and height. Never larger than the source.</returns>
    public static (int Width, int Height) ScaleToShortSide(int width, int height, int shortSide)
    {
        var shortest = Math.Min(width, height);

        if (shortest <= shortSide)
        {
            return (Even(width), Even(height));
        }

        var factor = (double)shortSide / shortest;

        return (Even((int)Math.Round(width * factor)), Even((int)Math.Round(height * factor)));
    }

    /// <summary>
    /// Lowers the video bitrate after an encode came out over target.
    /// </summary>
    /// <param name="videoBitrate">Bitrate used for the first try.</param>
    /// <param name="targetBytes">Target size.</param>
    /// <param name="actualBytes">Size the first try produced.</param>
    /// <returns>New bitrate, at least 1.</returns>
    public static uint RetryBitrate(uint videoBitrate, long targetBytes, long actualBytes) =>
        (uint)Math.Max(1, Math.Floor(videoBitrate * ((double)targetBytes / actualBytes)));

    private static List<(int Width, int Height)> CandidateSizes(int width, int height)
    {
        var sizes = new List<(int Width, int Height)> { ScaleToShortSide(width, height, int.MaxValue) };

        foreach (var tier in Tiers.Where(tier => tier < Math.Min(width, height)))
        {
            sizes.Add(ScaleToShortSide(width, height, tier));
        }

        return sizes;
    }

    private static double BitsPerPixel(double videoBitrate, (int Width, int Height) size, double frameRate) =>
        videoBitrate / (size.Width * (double)size.Height * frameRate);

    private static EncodePlan Encode((int Width, int Height) size, double video, uint audio) =>
        new(PlanKind.Encode, size.Width, size.Height, (uint)Math.Floor(video), audio, null);

    private static int Even(int value) => Math.Max(2, value & ~1);
}
