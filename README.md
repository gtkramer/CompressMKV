# MKV Helper

A multi-purpose CLI for batch processing MKV (and arbitrary other) video
files.  Three workflows live behind one binary:

- **`mkvhelper compress`** — VMAF-guided AV1 NVENC compression.  Detects
  whether the source is progressive, telecined, interlaced, or a mix;
  restores the original cadence; then searches for the highest-CQ encode
  that hits a per-frame VMAF quality target.  Implements the five NTSC
  content categories from the
  [MPlayer/MEncoder telecine guide §7.2](http://www.mplayerhq.hu/DOCS/HTML/en/menc-feat-telecine.html)
  and uses ffmpeg-native filters (`idet`, `fieldmatch`, `bwdif`,
  `decimate`) for both detection and restoration.

- **`mkvhelper split`** — slices a multi-episode MKV (a "season disc") into
  one MKV per episode by reading the source's chapter list, identifying
  main-content chapters via a duration threshold, and slicing on those
  boundaries with `mkvmerge`.

- **`mkvhelper print-chapters`** — pretty-prints an MKV's chapter list to
  the terminal: timestamps, durations, and which chapters would be
  classified as "main content" at a given threshold.  Use it as a dry run
  before `split` to pick a sensible threshold.

The `compress` workflow accepts **any** video file ffmpeg can read —
discovery probes every file in the input folder with ffprobe and keeps the
ones with real video content (skipping audio-only files, embedded cover
art, single-image files).  Output is always written as `.mkv` since
that's the right home for AV1 + multi-track audio + subtitle pass-through.

All three subcommands run their external tools (`ffmpeg`, `ffprobe`,
`mkvextract`, `mkvmerge`) inside a single bundled container — see
[Bundled dependency container](#bundled-dependency-container) below.  The
host doesn't need any of these installed separately; just `podman` and
the NVIDIA Container Toolkit.

## Repository layout

```
MkvHelper/
├── MkvHelper.sln                Solution file
├── Directory.Build.props        MSBuild settings shared by all projects
├── README.md                    This file
├── .github/                     CI workflows
├── MkvHelper.Cli/               Main app (executable: `mkvhelper`)
│   ├── Commands/                Spectre.Console.Cli subcommand classes
│   ├── Chapters/                MKV chapter XML models + mkvtoolnix wrapper
│   ├── Dependencies/            Container build orchestration + Containerfile
│   ├── Detection/               §7.2.2 classification (idet → 5 categories)
│   ├── Restore/                 §7.2.3 action selection + filter chains
│   ├── Encoding/                ffmpeg pipeline + AV1 NVENC encode + size guard
│   ├── Tuning/                  VMAF-guided CQ selection
│   ├── Infrastructure/          ffprobe, Fps, Proc utilities
│   ├── Models/                  summary/report DTOs
│   ├── Logging/                 Per-file decisions.log + live UI reporter
│   ├── Config.cs                Runtime configuration
│   └── Program.cs               CLI entry point
└── MkvHelper.Tests/             NUnit test suite
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

Every subcommand routes its external tooling through a Podman container
that needs the host's NVIDIA driver passed through.  Once per machine:

```bash
# 1. Install podman + the NVIDIA Container Toolkit.  See the storage-driver
#    note below for whether you also need `fuse-overlayfs`.
sudo pacman -S podman nvidia-container-toolkit

# 2. Generate the CDI spec so podman can resolve `nvidia.com/gpu=all`.
#    Re-run this after a driver upgrade.
sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml

# 3. Sanity check — should print `nvidia-smi` output from inside a container.
podman run --rm --device nvidia.com/gpu=all \
    docker.io/nvidia/cuda:12.8.1-base-ubuntu22.04 nvidia-smi

# 4. (Optional) Pre-warm the bundled container.  Takes 10–20 minutes
#    the first time.  If you skip this, the first `mkvhelper` run that
#    needs the container will build it automatically.
mkvhelper dependency build
```

### Container storage driver

Rootless podman picks its storage driver automatically the first time it
initialises `~/.local/share/containers/storage/`.  You don't need to
configure it from `mkvhelper`'s side — but the autodetect needs the right
packages to be present:

| `~/.local/share` filesystem | Extra package needed | Driver podman selects |
|-----------------------------|----------------------|------------------------|
| **btrfs**                   | none                 | kernel overlay (works natively over btrfs) |
| **ext4 / xfs**              | `fuse-overlayfs`     | fuse-overlayfs        |
| **anything else**           | `fuse-overlayfs`     | fuse-overlayfs        |

If `dependency build` exits with `Error: configure storage: kernel does
not support overlay fs: 'overlay' is not supported over extfs`, that's
the autodetect failing because `fuse-overlayfs` isn't installed and the
kernel won't let it use overlay-over-ext4 in a user namespace.  Install
`fuse-overlayfs` (`sudo pacman -S fuse-overlayfs`) and re-run.

## Run

```bash
# Top-level help
mkvhelper

# VMAF-guided compression
mkvhelper compress --input /path/to/input --output /path/to/output

# Split a multi-episode MKV into per-episode files
mkvhelper split --input season.mkv --series-name "My Show" --season-num 1
# → "My Show - S01E01.mkv", "My Show - S01E02.mkv", ... in the same dir

# Inspect chapters before splitting
mkvhelper print-chapters --input season.mkv --episode-chapter-threshold 600

# Dependency management (the bundled container)
mkvhelper dependency build       # build the container from the embedded Containerfile
mkvhelper dependency update      # rebuild only if Netflix tagged a newer VMAF release
mkvhelper dependency remove      # delete every built image and the state file
```

On the first invocation that needs the container, the build is kicked off
automatically (~10–20 minutes; image is reused on subsequent runs).

## Subcommand details

### `compress`

VMAF-guided AV1 NVENC encode.  Accepts a folder of inputs (any container
ffmpeg can read), classifies each by source type, applies the right
restoration filter, then runs a CQ ladder search until the encode hits the
configured VMAF thresholds (mean ≥ 97, p05 ≥ 95, p01 ≥ 90 by default).
Per-file output dir contains the final encode plus a `decisions.log` and
`log.json` for after-the-fact inspection.  See [VMAF on GPU](#vmaf-on-gpu)
below for how libvmaf_cuda fits in.

### `split`

Reads chapters via `mkvextract`, marks each chapter as "main" if its
duration is at or above `--episode-chapter-threshold` seconds (default 360),
and walks for main → non-main transitions.  Each transition closes one
episode at `i + --additional-chapters` (default 2), bundling trailing
credits with the episode they follow.  Output filenames follow
`{SeriesName} - S{Season:D2}E{Ep:D2}.mkv` and land beside the input file.

### `print-chapters`

Renders the chapter table to the terminal (Spectre.Console).  Same
threshold semantics as `split`, but doesn't touch the file — purely for
picking a sensible threshold before committing to a split.

## Bundled dependency container

mkvhelper ships a single Containerfile
([MkvHelper.Cli/Dependencies/Containerfile](MkvHelper.Cli/Dependencies/Containerfile))
that builds one image bundling every external tool the app shells out to:

| Tool | What's in the build |
|------|---------------------|
| **libvmaf** | Built from the pinned VMAF release with `-Denable_cuda=true` (provides `libvmaf_cuda`). |
| **FFmpeg** (pinned to a stable release tag) | NVENC, NVDEC, CUDA, libnpp; software AV1 via libdav1d (decode) + libaom + libsvtav1 (encode); x264, x265, libvpx; libopus, libvorbis, libmp3lame, libtheora; libass, libwebp, **libzimg** (zscale, for HDR tonemap). |
| **MKVToolNix** | `mkvextract`, `mkvmerge` for the chapter subcommands — installed from Ubuntu's `mkvtoolnix` apt package inside the container. |

Versions are pinned via build args inside the Containerfile (VMAF_TAG,
FFMPEG_TAG, NV_CODEC_TAG).  Bumping is a one-line edit + `mkvhelper
dependency build`.  `dependency update` queries Netflix/vmaf for the
latest release and rebuilds with that tag automatically.

The Containerfile is embedded in the binary as a managed resource
([EmbeddedResource entry in the csproj](MkvHelper.Cli/MkvHelper.Cli.csproj))
so the published binary is self-contained — no sibling file required at
install time.

The image is built on top of `nvidia/cuda:12.8.1-devel-ubuntu22.04`, which
ships nvcc 12.8 (Blackwell-aware) and the CUDA dev libraries at standard
paths.  `CPATH`/`LIBRARY_PATH` are exported so downstream `./configure`
and meson invocations auto-discover CUDA without per-call
`--extra-cflags=-I…` flags.

## VMAF on GPU

CPU libvmaf dominates the wall-clock cost of CQ tuning.  The compress
workflow runs VMAF on the GPU's CUDA cores via libvmaf_cuda — built into
our container per the upstream
[Netflix/vmaf docker recipe](https://github.com/Netflix/vmaf/blob/master/resource/doc/docker.md).
Throughput is large multiples of the CPU path on the same hardware.

The container has its own gate (`GpuGate.Cuda`) separate from the
dedicated NVENC/NVDEC engines, so VMAF jobs and NVENC sample encodes can
run concurrently without crowding each other off the silicon.

**HDR sources** route through the CPU libvmaf path by default
(`Config.UseCudaVmafForHdr = false`).  The HDR comparison needs zscale
tonemapping, which is CPU-only — running it on GPU is supported but the
zscale work still runs on CPU even then, so the only thing actually moved
to GPU is the libvmaf computation itself.  Flip the toggle if you want to
gate HDR on `GpuGate.Cuda` anyway (e.g. to free `CpuGate` slots for other
in-flight files).  SDR is always GPU.

## Artifact storage

```
~/.local/share/mkvhelper/      (XDG_DATA_HOME)
├── state.json                 # Built VMAF tag, image tag, build timestamp
└── build-logs/<tag>.log       # Captured stdout+stderr from podman build
```

The container image itself lives in Podman's storage
(`~/.local/share/containers/`) and is referenced by tag — `dependency
remove` cleans both.  Untagged intermediate layers from prior builds
(~10 GB worth) can be reclaimed afterward with `podman image prune -f`.

## Dependencies

- **.NET 10 SDK** (every project targets `net10.0`).
- **podman** + **NVIDIA Container Toolkit** + **fuse-overlayfs** (on
  ext4/xfs).  Everything mkvhelper actually shells out to —
  `ffmpeg`/`ffprobe`/`mkvextract`/`mkvmerge` — lives inside the bundled
  container, so the host doesn't need to install them separately.  Arch:
  ```bash
  sudo pacman -S podman nvidia-container-toolkit fuse-overlayfs
  sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml
  ```
- **NVIDIA GPU with NVENC + NVDEC** for production `compress` runs (tests
  do not require it).
