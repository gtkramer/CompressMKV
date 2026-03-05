namespace CompressMkv;

public sealed class Sampler
{
    private readonly Random _rng;
    public Sampler(int seed) => _rng = new Random(seed);

    /// <summary>
    /// Stratified random sampling: divides the video into <paramref name="count"/>
    /// equal segments and picks one random window per segment.
    /// Guarantees full temporal coverage — no clustering, no gaps.
    /// </summary>
    public List<SampleWindow> StratifiedRandomWindows(double durationSeconds, int count, int windowSeconds)
    {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        // Edge case: video shorter than one window.
        if (durationSeconds <= windowSeconds)
            return [new SampleWindow { StartSeconds = 0, LengthSeconds = Math.Min(windowSeconds, durationSeconds) }];

        double segLen = durationSeconds / count;
        var windows = new List<SampleWindow>(count);

        for (int i = 0; i < count; i++)
        {
            double segStart = i * segLen;
            double segEnd = segStart + segLen;

            // Ensure the window fits within the segment (or at least within the file).
            double maxStart = Math.Min(segEnd, durationSeconds) - windowSeconds;
            maxStart = Math.Max(segStart, maxStart);

            double t = (maxStart > segStart)
                ? segStart + _rng.NextDouble() * (maxStart - segStart)
                : segStart;
            t = Math.Max(0, Math.Min(t, durationSeconds - windowSeconds));

            windows.Add(new SampleWindow { StartSeconds = t, LengthSeconds = windowSeconds });
        }

        return windows;
    }
}
