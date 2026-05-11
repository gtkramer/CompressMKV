# CompressMKV

VMAF-guided AV1 NVENC compressor for arbitrary video files. Detects whether
the source is progressive, telecined, interlaced, or a mix; restores the
original cadence; then searches for the highest-CQ encode that hits a
per-frame VMAF quality target.

Despite the name, accepts **any** video file ffmpeg can read as input,
regardless of extension. Discovery probes every file in the input folder
with ffprobe and keeps the ones that contain real video content (excluding
audio-only files, embedded cover art, and single-image files). Output is
always written as `.mkv` since that's the right home for AV1 + multi-track
audio + subtitle pass-through.

Implements the five NTSC content categories from the
[MPlayer/MEncoder telecine guide §7.2](http://www.mplayerhq.hu/DOCS/HTML/en/menc-feat-telecine.html)
and uses ffmpeg-native filters (`idet`, `fieldmatch`, `bwdif`, `decimate`)
exclusively for both detection and restoration.

## Repository layout

```
CompressMKV/
├── CompressMKV.sln                Solution file
├── Directory.Build.props          MSBuild settings shared by all projects
├── README.md                      This file
├── .github/                       CI workflows
├── CompressMKV.CLI/               Main app (executable: `compressmkv`)
│   ├── Commands/                  Spectre.Console.Cli subcommand classes
│   ├── Dependencies/              Container build orchestration (podman, git, GitHub)
│   ├── Detection/                 §7.2.2 classification (idet → 5 categories)
│   ├── Restore/                   §7.2.3 action selection + filter chains
│   ├── Encoding/                  ffmpeg pipeline + AV1 NVENC encode + size guard
│   ├── Tuning/                    VMAF-guided CQ selection
│   ├── Infrastructure/            ffprobe, Fps, Proc utilities
│   ├── Models/                    summary/report DTOs
│   ├── Config.cs                  Runtime configuration
│   └── Program.cs                 CLI entry point
└── CompressMKV.Tests/             NUnit test suite
```

## Build & test

```bash
dotnet build
dotnet test                                   # all tests
dotnet test --filter "Category!=Integration"  # unit tests only (<100ms)
dotnet test --filter "Category=Integration"   # ffmpeg integration tests
```

The test suite is fully self-contained: every test clip is generated on the
fly via ffmpeg's `lavfi` source filter, so a fresh `git clone` + `dotnet test`
runs to green with no external test data.

## First-run setup

The GPU-accelerated VMAF pipeline runs inside a Podman container that needs
the host's NVIDIA driver passed through.  Once per machine:

```bash
# 1. Install podman + the NVIDIA Container Toolkit (Arch Linux).
#    podman is in [extra]; the toolkit is in [extra] as of 2024.
sudo pacman -S podman nvidia-container-toolkit

# 2. Generate the CDI spec so podman can resolve `nvidia.com/gpu=all`.
#    Re-run this after a driver upgrade.
sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml

# 3. Sanity check — should print `nvidia-smi` output from inside a container.
podman run --rm --device nvidia.com/gpu=all \
    docker.io/nvidia/cuda:12.4.0-base-ubuntu22.04 nvidia-smi

# 4. (Optional) Pre-warm the bundled ffmpeg+libvmaf_cuda container.  Takes
#    10–20 minutes the first time.  If you skip this, `compressmkv compress`
#    will run it automatically on first invocation.
compressmkv dependency build
```

If you can't or don't want to run a container, pass `--no-container` to any
`compress` invocation — VMAF will fall back to the system libvmaf on CPU
(much slower; functionally identical otherwise).

## Run

```bash
# Top-level help + subcommands
compressmkv

# Main compress workflow
compressmkv compress --input /path/to/input --output /path/to/output

# Dependency management (CUDA-enabled ffmpeg + libvmaf_cuda container)
compressmkv dependency build       # build the runtime container from Netflix/vmaf
compressmkv dependency update      # rebuild only if Netflix tagged a newer release
compressmkv dependency remove      # delete every built image + clone + state file
```

On first `compress` run with no container present, the build is kicked off
automatically (~10–20 minutes; image is reused on subsequent runs).  Pass
`--no-container` to fall back to the system ffmpeg/ffprobe (VMAF will run
on CPU instead of GPU — much slower).

## VMAF on GPU

CPU libvmaf dominates the wall-clock cost of CQ tuning.  This project ships
a containerised CUDA-enabled FFmpeg build (libvmaf_cuda) per the upstream
[Netflix/vmaf Dockerfiles](https://github.com/Netflix/vmaf/blob/master/resource/doc/docker.md);
it runs Phase 2 VMAF on the GPU's CUDA cores at large multiples of the
CPU path's throughput.

The container has its own gate (`GpuGate.Cuda`) separate from the dedicated
NVENC/NVDEC engines, so VMAF jobs and NVENC sample encodes can run
concurrently without crowding each other off the silicon.

**HDR sources** route through the CPU libvmaf path by default
(`Config.UseCudaVmafForHdr = false`).  The HDR comparison needs zscale
tonemapping, which is CPU-only — running it on GPU is supported but the
zscale work still runs on CPU even then, so the only thing actually moved
to GPU is the libvmaf computation itself.  Flip the toggle if you want to
gate HDR on `GpuGate.Cuda` anyway (e.g. to free `CpuGate` slots for other
in-flight files).  SDR is always GPU when the container is built.

## Artifact storage

All build artifacts live under `~/.local/share/compressmkv/` (XDG_DATA_HOME):

```
compressmkv/
├── state.json              # Built upstream tag, image tag, source path
├── vmaf/<tag>/             # Shallow clone of Netflix/vmaf at the built tag
└── build-logs/<tag>.log    # Captured stdout+stderr from podman build
```

The container images themselves live in Podman's storage
(`~/.local/share/containers/`) and are referenced by tag — `dependency
remove` cleans both.

## Dependencies

- .NET 10 SDK
- For container mode (recommended): `podman` and the **NVIDIA Container Toolkit**.
  On Arch Linux:
  ```bash
  sudo pacman -S podman nvidia-container-toolkit
  sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml
  ```
- For native fallback (`--no-container`): `ffmpeg` and `ffprobe` on `$PATH`,
  with `libvmaf`, `libzimg`, `idet`, `fieldmatch`, `bwdif`, and `decimate`.
- NVIDIA GPU with NVENC + NVDEC for production runs (tests do not require it).
