namespace MkvHelper;

public sealed record PreviewArtifact
{
    public double TimestampSeconds { get; init; }
    public string IvtcPath { get; init; } = "";
    public string DeintPath { get; init; } = "";
}
