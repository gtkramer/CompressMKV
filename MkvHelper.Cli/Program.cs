// Program.cs
//
// Entry point for the `mkvhelper` CLI.  Subcommands:
//
//   mkvhelper compress              — VMAF-guided AV1 NVENC encode for a folder of files
//   mkvhelper split                 — slice a multi-episode MKV into one MKV per episode
//   mkvhelper print-chapters        — pretty-print an MKV's chapter list
//   mkvhelper dependency build      — build the bundled CUDA-enabled ffmpeg+VMAF container
//   mkvhelper dependency update     — rebuild only if Netflix/vmaf has a newer release
//   mkvhelper dependency remove     — remove all built artifacts (images, source clones, state)
//
// On first `compress` run, if no container build exists, mkvhelper will
// build it automatically (Netflix/vmaf Dockerfile via podman) so VMAF can
// run on the GPU via libvmaf_cuda.  Pass `--no-container` to fall back to
// the system ffmpeg/ffprobe (VMAF will run on CPU — much slower).

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

            c.AddBranch("dependency", dep =>
            {
                dep.SetDescription("Manage the bundled CUDA-enabled ffmpeg+VMAF container build.");

                dep.AddCommand<DependencyBuildCommand>("build")
                    .WithDescription("Build the container image from the Netflix/vmaf Dockerfile.");

                dep.AddCommand<DependencyUpdateCommand>("update")
                    .WithDescription("Rebuild only if Netflix/vmaf has tagged a newer release.");

                dep.AddCommand<DependencyRemoveCommand>("remove")
                    .WithDescription("Remove all built container images, source clones, and state.");
            });
        });
        return app.RunAsync(args);
    }
}
