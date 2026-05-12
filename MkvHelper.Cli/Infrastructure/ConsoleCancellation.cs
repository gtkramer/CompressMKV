namespace MkvHelper;

/// <summary>
/// Builds a <see cref="CancellationTokenSource"/> linked to the parent token
/// and to console Ctrl+C.  First Ctrl+C is intercepted (the CTS cancels);
/// subsequent Ctrl+C falls through to the runtime's default handler so a
/// stuck process can still be hard-killed from the terminal.
/// </summary>
internal static class ConsoleCancellation
{
    public static CancellationTokenSource LinkToConsole(CancellationToken parent)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return cts;
    }
}
