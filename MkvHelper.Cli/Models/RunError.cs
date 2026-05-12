namespace MkvHelper;

public sealed record RunError
{
    public string File { get; init; } = "";
    public string Error { get; init; } = "";
}
