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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var state = await ContainerBuilder.BuildAsync(
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

// ----------------------------------------------------------------
//  mkvhelper container remove
// ----------------------------------------------------------------

public sealed class ContainerRemoveSettings : CommandSettings
{
    [CommandOption("-y|--yes")]
    [Description("Skip the confirmation prompt.")]
    public bool Yes { get; init; }
}

public sealed class ContainerRemoveCommand : AsyncCommand<ContainerRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ContainerRemoveSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        AnsiConsole.MarkupLine(
            $"This will remove the [bold]{Markup.Escape(ContainerBuilder.ImageTag)}[/] image, " +
            "the build log, and the state file.  Untagged intermediate layers from prior " +
            "builds are NOT removed (use `podman image prune -f` for those).");

        if (!settings.Yes)
        {
            if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                return 2;
            }
        }

        try
        {
            await ContainerBuilder.RemoveAllAsync(
                onProgress: line => AnsiConsole.MarkupLine($"[grey]›[/] {Markup.Escape(line)}"),
                ct: cts.Token);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Remove failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
