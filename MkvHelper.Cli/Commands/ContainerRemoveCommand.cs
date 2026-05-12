using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MkvHelper;

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
        using CancellationTokenSource cts = ConsoleCancellation.LinkToConsole(token);

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
