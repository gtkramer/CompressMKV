// Program.cs
//
// Entry point for the `mkvhelper` CLI.  Subcommands:
//
//   mkvhelper compress              — VMAF-guided AV1 NVENC encode for a folder of files
//   mkvhelper split                 — slice a multi-episode MKV into one MKV per episode
//   mkvhelper print-chapters        — pretty-print an MKV's chapter list
//   mkvhelper container build       — build the bundled dependency container from the embedded Containerfile
//   mkvhelper container remove      — remove the built image, build log, and state file
//
// On any subcommand that needs the container, mkvhelper auto-builds it on
// first use (or when the embedded Containerfile has changed since the
// last build).  Subsequent runs reuse the image.

using Spectre.Console.Cli;

namespace MkvHelper;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(c =>
        {
            c.SetApplicationName("mkvhelper");

            c.AddCommand<CompressCommand>("compress")
                .WithDescription("VMAF-guided AV1 NVENC compression for a folder of video files.")
                .WithExample("compress", "--input", "/videos/in", "--output", "/videos/out");

            c.AddCommand<SplitCommand>("split")
                .WithDescription("Slice a multi-episode MKV into one MKV per episode using the source's chapter list.")
                .WithExample("split", "--input", "season.mkv", "--series-name", "My Show", "--season-num", "1");

            c.AddCommand<PrintChaptersCommand>("print-chapters")
                .WithDescription("Print an MKV's chapter list (timestamps, durations, main-content classification).")
                .WithExample("print-chapters", "--input", "season.mkv");

            c.AddBranch("container", cont =>
            {
                cont.SetDescription("Manage the bundled dependency container (CUDA-enabled FFmpeg + libvmaf_cuda + MKVToolNix).");

                cont.AddCommand<ContainerBuildCommand>("build")
                    .WithDescription("Build the dependency container from the embedded Containerfile.");

                cont.AddCommand<ContainerRemoveCommand>("remove")
                    .WithDescription("Remove the built image, build log, and state file.");
            });
        });
        return app.RunAsync(args);
    }
}
