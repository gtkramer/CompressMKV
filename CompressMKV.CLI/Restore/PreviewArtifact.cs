namespace CompressMkv;

public sealed class PreviewArtifact
{
    public double TimestampSeconds { get; set; }
    public string IvtcPath { get; set; } = "";
    public string DeintPath { get; set; } = "";
}
