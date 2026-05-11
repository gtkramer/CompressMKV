using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CompressMkv;

// ----------------------------------------------------------------
//  compressmkv dependency build [--tag <vX.Y.Z>]
// ----------------------------------------------------------------

public sealed class DependencyBuildSettings : CommandSettings
{
    [CommandOption("--tag <TAG>")]
    [Description("Build a specific Netflix/vmaf release tag instead of the latest.  E.g. v3.0.0.")]
    public string? Tag { get; init; }
}

public sealed class DependencyBuildCommand : AsyncCommand<DependencyBuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DependencyBuildSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var state = await VmafBuilder.BuildAsync(
                tag: settings.Tag,
                onProgress: line => AnsiConsole.MarkupLine($"[grey]›[/] {Markup.Escape(line)}"),
                ct: cts.Token);
            AnsiConsole.MarkupLine($"[green]Build complete:[/] {Markup.Escape(state.ImageTag)} from {Markup.Escape(state.UpstreamTag)}.");
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
//  compressmkv dependency update
// ----------------------------------------------------------------

public sealed class DependencyUpdateSettings : CommandSettings { }

public sealed class DependencyUpdateCommand : AsyncCommand<DependencyUpdateSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DependencyUpdateSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var state = await BuildState.LoadAsync(cts.Token);
            AnsiConsole.MarkupLine("[grey]Querying Netflix/vmaf for the latest release tag...[/]");
            var latest = await ReleaseFetcher.GetLatestAsync(cts.Token);
            AnsiConsole.MarkupLine($"  Latest:   [bold]{Markup.Escape(latest.Tag)}[/] (published {latest.PublishedUtc:yyyy-MM-dd})");
            AnsiConsole.MarkupLine(
                "  Local:    " +
                (state is null
                    ? "[yellow]none[/]"
                    : $"[bold]{Markup.Escape(state.UpstreamTag)}[/] (built {state.BuiltUtc:yyyy-MM-dd})"));

            if (state is not null && string.Equals(state.UpstreamTag, latest.Tag, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[green]Already up to date — nothing to do.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[yellow]Building {Markup.Escape(latest.Tag)}...[/]");
            var newState = await VmafBuilder.BuildAsync(
                tag: latest.Tag,
                onProgress: line => AnsiConsole.MarkupLine($"[grey]›[/] {Markup.Escape(line)}"),
                ct: cts.Token);
            AnsiConsole.MarkupLine($"[green]Updated to {Markup.Escape(newState.UpstreamTag)}.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Update failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}

// ----------------------------------------------------------------
//  compressmkv dependency remove
// ----------------------------------------------------------------

public sealed class DependencyRemoveSettings : CommandSettings
{
    [CommandOption("-y|--yes")]
    [Description("Skip the confirmation prompt.")]
    public bool Yes { get; init; }
}

public sealed class DependencyRemoveCommand : AsyncCommand<DependencyRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DependencyRemoveSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        AnsiConsole.MarkupLine(
            "This will remove all compressmkv build artifacts: every container " +
            $"image we built, source clones under {Markup.Escape(ArtifactPaths.VmafSourceRoot)}, " +
            "and the build-state file.");

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
            await VmafBuilder.RemoveAllAsync(
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
