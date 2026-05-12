# mkvhelper

A single `mkvhelper` CLI for batch-processing MKV (and arbitrary other) video
files, with three subcommands:

| Subcommand | What it does |
|---|---|
| `mkvhelper compress` | VMAF-guided AV1 NVENC encode for a folder of video files.  Detects whether each source is progressive, telecined, interlaced, or a mix; restores the original cadence; then runs a CQ search until the encode meets per-frame VMAF quality gates (mean ≥ 97, p05 ≥ 95, p01 ≥ 90 by default).  Implements the five NTSC content categories from the [MPlayer/MEncoder telecine guide §7.2](http://www.mplayerhq.hu/DOCS/HTML/en/menc-feat-telecine.html) using ffmpeg-native filters (`idet`, `fieldmatch`, `bwdif`, `decimate`) for both detection and restoration. |
| `mkvhelper split` | Slices a multi-episode MKV (a "season disc") into one MKV per episode.  Reads the source's chapter list, identifies main-content chapters via a duration threshold, then slices on those boundaries with `mkvmerge`.  Output filenames follow `{Series} - S{Season:D2}E{Ep:D2}.mkv`. |
| `mkvhelper print-chapters` | Pretty-prints an MKV's chapter list (timestamps, durations, main-content classification).  Use it as a dry run before `split` to pick a sensible threshold. |
| `mkvhelper container …` | Manage the bundled dependency container — see below. |

The `compress` workflow accepts **any** video file ffmpeg can read.  Discovery
probes every file under the input folder with ffprobe and keeps the ones with
real video content (skipping audio-only files, embedded cover art, single-image
files).  Output is always written as `.mkv` — the right home for AV1 +
multi-track audio + subtitle pass-through.

All ffmpeg / ffprobe / mkvextract / mkvmerge invocations run **inside a single
bundled container** built from `MkvHelper.Cli/Dependencies/Containerfile`.  The
host doesn't need those tools installed separately — only `podman`, the NVIDIA
Container Toolkit, and (on ext4/xfs) `fuse-overlayfs`.  See [What you need
to install](#what-you-need-to-install).

## What you need to install

mkvhelper deliberately keeps the host install surface tiny.  Everything below
is the **complete** list — no other tooling needs to be on the host.

### To build the project

| Software | Why | Arch package |
|---|---|---|
| **.NET 10 SDK** | All projects target `net10.0`. | `dotnet-sdk` |
| **git** | Cloning + repo hygiene. | `git` |

```bash
git clone https://github.com/<your-fork>/CompressMKV.git
cd CompressMKV
dotnet build
```

That's it for "can I build the source."  No host ffmpeg, no host MKVToolNix,
no CUDA toolkit on the host.  The .NET solution restores its NuGet
dependencies (`Spectre.Console`, `Serilog`, `NUnit`) automatically on first
build.

### To run mkvhelper and the test suite

The CLI subcommands and the integration test suite both run their external
tools (`ffmpeg`, `ffprobe`, `mkvextract`, `mkvmerge`, `libvmaf_cuda`) inside
a Podman container.  That container is built from
[MkvHelper.Cli/Dependencies/Containerfile](MkvHelper.Cli/Dependencies/Containerfile)
the first time something needs it (10–20 min cold; reused thereafter).

| Software | Why | Arch package |
|---|---|---|
| **podman** | Runs the dependency container.  Rootless is fine. | `podman` |
| **NVIDIA Container Toolkit** | Lets podman expose GPU devices to the container (NVENC, NVDEC, CUDA). | `nvidia-container-toolkit` |
| **fuse-overlayfs** *(ext4/xfs only)* | Rootless podman's storage driver picks this when `~/.local/share` lives on ext4 or xfs.  Not needed on btrfs. | `fuse-overlayfs` |
| **NVIDIA driver** | Provides `nvidia-smi` (used for system-utilization sampling) and the kernel modules the toolkit passes through.  Driver-installed; nothing to install separately if your GPU is already working. | (your driver package) |

One-time setup after installing the above:

```bash
# 1. Generate the CDI spec so podman can resolve `nvidia.com/gpu=all`.
#    Re-run this whenever you upgrade the NVIDIA driver.
sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml

# 2. Sanity check — should print nvidia-smi output from inside a container.
podman run --rm --device nvidia.com/gpu=all \
    docker.io/nvidia/cuda:12.8.1-base-ubuntu22.04 nvidia-smi

# 3. (Optional) Pre-warm the dependency container.  If you skip this,
#    the first mkvhelper subcommand (or `dotnet test`) that needs the
#    container will auto-build it.
mkvhelper container build
```

#### A note on storage drivers

Rootless podman picks its storage driver automatically the first time it
initialises `~/.local/share/containers/storage/`.  The autodetect needs the
right host packages to be present:

| `~/.local/share` filesystem | Extra package | Driver podman picks |
|---|---|---|
| **btrfs** | none | kernel overlay (works natively over btrfs) |
| **ext4 / xfs** | `fuse-overlayfs` | fuse-overlayfs |
| **other** | `fuse-overlayfs` | fuse-overlayfs |

If `container build` exits with `Error: configure storage: kernel does not
support overlay fs: 'overlay' is not supported over extfs`, install
`fuse-overlayfs` and re-run.

### To run `mkvhelper compress` against real content

| Hardware | Why |
|---|---|
| **NVIDIA GPU with NVENC + NVDEC** (Turing or newer; tested on RTX 5080) | The final encode is AV1 NVENC; VMAF measurement runs on CUDA cores via `libvmaf_cuda`.  Older GPUs without AV1 NVENC can't run the production path. |

The test suite does NOT require a GPU for compilation or unit tests, but the
integration tests do hit the container's ffmpeg, which expects CUDA at
runtime.

## Build and test

```bash
dotnet build
dotnet test --filter "Category!=Integration"  # unit tests only (<1 s)
dotnet test --filter "Category=Integration"   # integration tests (need container)
dotnet test                                   # all tests
```

Unit tests are entirely self-contained — pure logic, no external processes.
They run in under a second and don't touch the container.

Integration tests live in the `MkvHelper.Tests.Integration` namespace and
synthesise their test clips on the fly via ffmpeg's `lavfi` source filter.
A fresh `git clone` + `dotnet test` against the integration suite triggers
the same container auto-build production code does — first run takes the
container build time, subsequent runs reuse the image.

## How to run the subcommands

```bash
# Top-level help
mkvhelper

# VMAF-guided compression
mkvhelper compress --input /path/to/input --output /path/to/output

# Split a multi-episode MKV into per-episode files
mkvhelper split --input season.mkv --series-name "My Show" --season-num 1
# → "My Show - S01E01.mkv", "My Show - S01E02.mkv", … in the same dir

# Inspect chapters before splitting
mkvhelper print-chapters --input season.mkv --episode-chapter-threshold 600

# Container management
mkvhelper container status              # report ready / stale / missing / unbuilt
mkvhelper container build               # build from the embedded Containerfile
mkvhelper container build --no-cache    # full rebuild, ignoring podman's layer cache
mkvhelper container remove              # remove built image + build log + state file
```

The first subcommand invocation that needs the container kicks off a build
automatically (~10–20 min on a cold cache).  If you edit the embedded
Containerfile, the next subcommand notices the SHA-256 mismatch and
auto-rebuilds — you don't have to invoke `container build` manually.

`mkvhelper container status` shows the current state without doing
anything (exit code 0 = ready, non-zero = a rebuild would happen next).
`--quiet` suppresses output and just sets the exit code, useful in scripts:

```bash
if ! mkvhelper container status --quiet; then
    mkvhelper container build
fi
```

## Subcommand details

### `compress`

VMAF-guided AV1 NVENC encode.  Accepts a folder of inputs (any container
ffmpeg can read), classifies each by source type, applies the right
restoration filter, then runs a binary search over the CQ range until the
encode hits the configured VMAF gates.  Per-file output dir contains the
final encode plus a `decisions.log` and `log.json` for after-the-fact
inspection.  See [VMAF on GPU](#vmaf-on-gpu) for how `libvmaf_cuda` fits in.

### `split`

Reads chapters via `mkvextract`, marks each chapter as "main" if its
duration is at or above `--episode-chapter-threshold` seconds (default 360),
and walks for main → non-main transitions.  Each transition closes one
episode at `i + --additional-chapters` (default 2), bundling trailing
credits with the episode they follow.

### `print-chapters`

Renders the chapter table to the terminal (Spectre.Console).  Same
threshold semantics as `split`, but doesn't touch the file — purely for
picking a sensible threshold before committing to a split.

## Repository layout

```
CompressMKV/
├── MkvHelper.sln                Solution file
├── Directory.Build.props        MSBuild settings shared by all projects
├── README.md                    This file
├── .github/                     CI workflows
├── MkvHelper.Cli/               Main app (executable: `mkvhelper`)
│   ├── Program.cs               CLI entry point
│   ├── Config.cs                Runtime configuration
│   ├── Commands/                Spectre.Console.Cli subcommand classes
│   ├── Chapters/                MKV chapter XML models + MKVToolNix wrapper
│   ├── Dependencies/            Container build orchestration + Containerfile
│   ├── Detection/               §7.2.2 classification (idet → five categories)
│   ├── Restore/                 §7.2.3 action selection + filter chains
│   ├── Encoding/                ffmpeg pipeline + AV1 NVENC encode + size guard
│   ├── Verification/            Post-encode trust-but-verify pass
│   ├── Tuning/                  VMAF-guided CQ selection
│   ├── Infrastructure/          ffprobe, Fps, Proc, ResourcePool, SystemSampler
│   ├── Models/                  Summary / report DTOs (log.json shape)
│   └── Logging/                 Per-file decisions.log + live UI reporter
└── MkvHelper.Tests/             NUnit test suite
    └── (Integration namespace)  Tests that route through the dependency container
```

## Bundled dependency container

mkvhelper ships a single Containerfile
([MkvHelper.Cli/Dependencies/Containerfile](MkvHelper.Cli/Dependencies/Containerfile))
that builds one image bundling every external tool the app shells out to:

| Tool | What's in the build |
|---|---|
| **libvmaf** | Built from the pinned VMAF release with `-Denable_cuda=true` (provides `libvmaf_cuda`). |
| **FFmpeg** (pinned to a stable release tag) | NVENC, NVDEC, CUDA, libnpp; software AV1 via libdav1d (decode) + libaom (encode); x264, x265, libvpx; libopus, libvorbis, libmp3lame, libtheora; libass, libwebp, **libzimg** (zscale, for HDR tonemap), libfreetype+libfribidi (drawtext, used by the integration-test clip builder). |
| **MKVToolNix** | `mkvextract`, `mkvmerge` for the chapter subcommands — installed from Ubuntu's `mkvtoolnix` apt package inside the container. |

Versions are pinned via build args inside the Containerfile (`VMAF_TAG`,
`FFMPEG_TAG`, `NV_CODEC_TAG`).  Bumping a version is a one-line edit; the
next subcommand that needs the container notices the SHA-256 change and
auto-rebuilds.

The Containerfile is embedded in the binary as a managed resource
([EmbeddedResource entry in the csproj](MkvHelper.Cli/MkvHelper.Cli.csproj))
so the published binary is self-contained — no sibling file required at
install time.

The image is built on top of `nvidia/cuda:12.8.1-devel-ubuntu24.04`, which
ships nvcc 12.8 (Blackwell-aware) and the CUDA dev libraries at standard
paths.  `CPATH`/`LIBRARY_PATH` are exported so downstream `./configure`
and meson invocations auto-discover CUDA without per-call
`--extra-cflags=-I…` flags.

## VMAF on GPU

CPU libvmaf dominates the wall-clock cost of CQ tuning.  The compress
workflow runs VMAF on the GPU's CUDA cores via `libvmaf_cuda` — built into
our container per the upstream
[Netflix/vmaf docker recipe](https://github.com/Netflix/vmaf/blob/master/resource/doc/docker.md).
Throughput is large multiples of the CPU path on the same hardware.

The container has its own gate (`ResourcePool` CUDA lanes) separate from
the dedicated NVENC/NVDEC engines, so VMAF jobs and NVENC sample encodes
can run concurrently without crowding each other off the silicon.

**HDR sources** route through the CPU libvmaf path by default
(`Config.UseCudaVmafForHdr = false`).  The HDR comparison needs zscale
tonemapping, which is CPU-only — running it on GPU is supported but the
zscale work still runs on CPU even then, so the only thing actually moved
to GPU is the libvmaf computation itself.  SDR is always GPU.

## Artifact storage

```
~/.local/share/mkvhelper/      (XDG_DATA_HOME)
├── state.json                 # Image tag, Containerfile SHA-256, build timestamp
└── build.log                  # Captured stdout+stderr from the most recent podman build
```

The container image itself lives in Podman's storage
(`~/.local/share/containers/`) under the fixed tag
`localhost/mkvhelper:current`; rebuilding replaces it in place.
`container remove` cleans the image + state file but does NOT prune
orphaned intermediate layers (~10 GB worth) — use `podman image prune -f`
for that.
