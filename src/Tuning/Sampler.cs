namespace CompressMkv;

public sealed class Sampler
{
    private readonly Random _rng;
    public Sampler(int seed) => _rng = new Random(seed);

    public List<SampleWindow> RandomWindows(double durationSeconds, int count, int windowSeconds)
    {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        double maxStart = Math.Max(0, durationSeconds - windowSeconds);
        var windows = new List<SampleWindow>(count);

        for (int i = 0; i < count; i++)
        {
            double t = _rng.NextDouble() * maxStart;
            windows.Add(new SampleWindow
            {
                StartSeconds = t,
                LengthSeconds = windowSeconds
            });
        }
        return windows;
    }
}
