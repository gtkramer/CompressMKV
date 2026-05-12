using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MkvHelper;

// ----------------------------------------------------------------
//  mkvhelper container build [--no-cache]
// ----------------------------------------------------------------

public sealed class ContainerBuildSettings : CommandSettings
{
    [CommandOption("--no-cache")]
    [Description("Bypass podman's layer cache and rebuild every step from scratch.  Use when apt or git would now resolve to newer versions of pinned-by-name packages and you want them picked up.")]
    public bool NoCache { get; init; }
}

public sealed class ContainerBuildCommand : AsyncCommand<ContainerBuildSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ContainerBuildSettings settings, CancellationToken token)
    {
        using CancellationTokenSource cts = ConsoleCancellation.LinkToConsole(token);

        try
        {
            BuildState state = await ContainerBuilder.BuildAsync(
                noCache: settings.NoCache,
                onProgress: line => AnsiConsole.MarkupLine($"[grey]›[/] {Markup.Escape(line)}"),
                ct: cts.Token);
            AnsiConsole.MarkupLine(
                $"[green]Build complete:[/] {Markup.Escape(state.ImageTag)} (built {state.BuiltUtc:yyyy-MM-dd HH:mm:ss}Z).");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Build failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
