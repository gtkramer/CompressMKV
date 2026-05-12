namespace MkvHelper;

public sealed record SampleWindow
{
    public double StartSeconds { get; init; }
    public double LengthSeconds { get; init; }
}
