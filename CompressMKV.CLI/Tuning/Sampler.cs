namespace CompressMkv;

/// <summary>
/// Stratified random sampler for VMAF measurement windows.  Adapts to short
/// sources so we still produce reliable measurements when the source can't
/// fit the full configured sample budget.
///
/// Three regimes, chosen by source duration D and the configured (count,
/// windowSeconds):
///
///   D &lt; 2·windowSeconds (very short)
///     One window covering the whole source.  Cheaper and more accurate than
///     trying to fit overlapping samples — we measure 100% of the content.
///     Below ~5s the measurement is dominated by encoder rate-control warm-up,
///     but that's the source's limit, not ours.
///
///   2·windowSeconds ≤ D &lt; count·windowSeconds (medium)
///     Window length stays at <paramref name="windowSeconds"/> so each window
///     is long enough for stable VMAF measurement (rc-lookahead settles).
///     Count scales down to floor(D / windowSeconds), guaranteeing
///     non-overlapping windows.  Stratified random within each segment.
///
///   D ≥ count·windowSeconds (long)
///     Standard sampling: count × windowSeconds, evenly stratified.  Each
///     segment is segLen ≥ windowSeconds, so windows don't overlap and have
///     gap room for randomization.
///
/// Window length is preferred-fixed because the statistical reliability of a
/// VMAF window depends on its length (encoder steady-state + per-frame VMAF
/// noise averaging out).  Scaling the count instead preserves per-window
/// quality at the cost of fewer total samples on short content.
/// </summary>
public sealed class Sampler
{
    private readonly Random _rng;
    public Sampler(int seed) => _rng = new Random(seed);

    /// <summary>
    /// Builds a list of stratified random sample windows.  Returned list size
    /// may be less than <paramref name="count"/> when the source is too short
    /// to fit that many non-overlapping windows; callers should log when
    /// adaptive scaling kicked in.
    /// </summary>
    public List<SampleWindow> StratifiedRandomWindows(double durationSeconds, int count, int windowSeconds)
    {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        // Very short content: measuring everything is cheaper than trying to
        // sample, and removes any sampling-extrapolation error.  The threshold
        // (2× window length) is the point at which we could fit at most two
        // non-overlapping windows.
        if (durationSeconds < windowSeconds * 2.0)
        {
            return [new SampleWindow { StartSeconds = 0, LengthSeconds = durationSeconds }];
        }

        // Cap the count to whatever fits non-overlapping at the configured
        // window length.  Long sources pass through unchanged at 'count';
        // medium-length sources get fewer windows but each at full length,
        // which keeps per-window VMAF reliable.
        int maxNonOverlapping = (int)Math.Floor(durationSeconds / windowSeconds);
        int actualCount = Math.Min(count, maxNonOverlapping);

        double segLen = durationSeconds / actualCount;
        var windows = new List<SampleWindow>(actualCount);

        for (int i = 0; i < actualCount; i++)
        {
            double segStart = i * segLen;
            double segEnd = segStart + segLen;

            // The last legal start within this segment that still lets the
            // window fit inside [segStart, durationSeconds].
            double maxStart = Math.Min(segEnd, durationSeconds) - windowSeconds;
            maxStart = Math.Max(segStart, maxStart);

            double t = (maxStart > segStart)
                ? segStart + _rng.NextDouble() * (maxStart - segStart)
                : segStart;

            // Defensive clamp: the window must fit entirely within [0, duration].
            t = Math.Max(0, Math.Min(t, durationSeconds - windowSeconds));

            windows.Add(new SampleWindow { StartSeconds = t, LengthSeconds = windowSeconds });
        }

        return windows;
    }
}
