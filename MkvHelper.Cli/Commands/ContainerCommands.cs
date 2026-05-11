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
//  mkvhelper container status
// ----------------------------------------------------------------

public sealed class ContainerStatusSettings : CommandSettings
{
    [CommandOption("-q|--quiet")]
    [Description("Print nothing; just exit 0 if the container is ready, non-zero otherwise.  For scripts.")]
    public bool Quiet { get; init; }
}

/// <summary>
/// Reports the current state of the dependency container without
/// touching it: image presence in podman, last-built timestamp, the
/// stored vs. embedded Containerfile SHAs, and a single overall
/// readiness verdict.  Exit code 0 = ready, non-zero = a rebuild would
/// happen on the next subcommand invocation.
/// </summary>
public sealed class ContainerStatusCommand : AsyncCommand<ContainerStatusSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ContainerStatusSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        ContainerBuilder.ContainerStatus status;
        try
        {
            status = await ContainerBuilder.GetStatusAsync(cts.Token);
        }
        catch (Exception ex)
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[red]Status check failed:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        int exitCode = status.Readiness == ContainerBuilder.ContainerReadiness.Ready ? 0 : 1;
        if (settings.Quiet)
            return exitCode;

        Render(status);
        return exitCode;
    }

    private static void Render(ContainerBuilder.ContainerStatus status)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        grid.AddRow(new Markup("[grey]Image tag:[/]"),
                    new Markup($"[bold]{Markup.Escape(status.ImageTag)}[/]"));

        grid.AddRow(new Markup("[grey]Image present:[/]"),
                    new Markup(status.ImageExists ? "[green]yes[/]" : "[red]no[/]"));

        if (status.State is { } state)
        {
            grid.AddRow(new Markup("[grey]Last built:[/]"),
                        new Markup($"{state.BuiltUtc:yyyy-MM-dd HH:mm:ss}Z [dim]({FormatRelative(DateTime.UtcNow - state.BuiltUtc)})[/]"));
            grid.AddRow(new Markup("[grey]Stored SHA:[/]"),
                        new Markup($"[dim]{Markup.Escape(ShortSha(state.ContainerfileSha256))}[/]"));
        }
        else
        {
            grid.AddRow(new Markup("[grey]Last built:[/]"), new Markup("[dim]never[/]"));
            grid.AddRow(new Markup("[grey]Stored SHA:[/]"), new Markup("[dim]none[/]"));
        }

        grid.AddRow(new Markup("[grey]Embedded SHA:[/]"),
                    new Markup($"[dim]{Markup.Escape(ShortSha(status.CurrentContainerfileSha))}[/]"));
        grid.AddRow(new Markup("[grey]SHA match:[/]"),
                    new Markup(status.ShaMatches ? "[green]yes[/]" : "[red]no[/]"));

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

        var (verdict, hint) = status.Readiness switch
        {
            ContainerBuilder.ContainerReadiness.Ready =>
                ("[green bold]READY[/]", "Next subcommand will use the cached image."),
            ContainerBuilder.ContainerReadiness.Stale =>
                ("[yellow bold]STALE[/]", "The embedded Containerfile has changed since the last build.  Next subcommand will auto-rebuild."),
            ContainerBuilder.ContainerReadiness.ImageMissing =>
                ("[yellow bold]IMAGE MISSING[/]", $"State references {Markup.Escape(status.ImageTag)} but it's gone from podman storage.  Next subcommand will auto-rebuild."),
            ContainerBuilder.ContainerReadiness.NotBuilt =>
                ("[red bold]NOT BUILT[/]", "No container has been built yet.  Next subcommand will auto-build, or you can pre-warm with `mkvhelper container build`."),
            _ =>
                ("[red bold]UNKNOWN[/]", ""),
        };

        AnsiConsole.MarkupLine($"Status: {verdict}");
        if (!string.IsNullOrEmpty(hint))
            AnsiConsole.MarkupLine($"  [grey]{hint}[/]");
    }

    /// <summary>First 12 hex chars + ellipsis — enough to spot a difference at a glance,
    /// short enough not to wrap a terminal line.</summary>
    private static string ShortSha(string sha)
    {
        if (string.IsNullOrEmpty(sha)) return "(none)";
        return sha.Length > 12 ? sha[..12] + "…" : sha;
    }

    private static string FormatRelative(TimeSpan ago)
    {
        if (ago.TotalSeconds < 60) return "just now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 30) return $"{(int)ago.TotalDays}d ago";
        if (ago.TotalDays < 365) return $"{(int)(ago.TotalDays / 30)}mo ago";
        return $"{(int)(ago.TotalDays / 365)}y ago";
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
