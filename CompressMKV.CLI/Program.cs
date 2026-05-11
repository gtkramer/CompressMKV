// Program.cs
//
// Entry point for the `compressmkv` CLI.  Subcommands:
//
//   compressmkv compress              — main encode workflow (was the default in older versions)
//   compressmkv dependency build      — build the bundled CUDA-enabled ffmpeg+VMAF container
//   compressmkv dependency update     — rebuild only if Netflix/vmaf has a newer release
//   compressmkv dependency remove     — remove all built artifacts (images, source clones, state)
//
// On first `compress` run, if no container build exists, compressmkv will
// build it automatically (Netflix/vmaf Dockerfile via podman) so VMAF can
// run on the GPU via libvmaf_cuda.  Pass `--no-container` to fall back to
// the system ffmpeg/ffprobe (VMAF will run on CPU — much slower).

using Spectre.Console.Cli;

namespace CompressMkv;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(c =>
        {
            c.SetApplicationName("compressmkv");

            c.AddCommand<CompressCommand>("compress")
                .WithDescription("VMAF-guided AV1 NVENC compression for a folder of video files.")
                .WithExample("compress", "--input", "/videos/in", "--output", "/videos/out");

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
