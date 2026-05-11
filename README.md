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
│   ├── Dependencies/            Container build orchestration (podman, git, GitHub)
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

The GPU-accelerated VMAF pipeline (`compress`) runs inside a Podman container
that needs the host's NVIDIA driver passed through.  Once per machine:

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
#    10–20 minutes the first time.  If you skip this, `mkvhelper compress`
#    will run it automatically on first invocation.
mkvhelper dependency build
```

If you can't or don't want to run a container, pass `--no-container` to any
`compress` invocation — VMAF will fall back to the system libvmaf on CPU
(much slower; functionally identical otherwise).

The `split` and `print-chapters` subcommands shell out to MKVToolNix
(`mkvextract`, `mkvmerge`) and have no container dependency.  On Arch
Linux: `sudo pacman -S mkvtoolnix-cli`.

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

# Dependency management (CUDA-enabled ffmpeg + libvmaf_cuda container)
mkvhelper dependency build       # build the runtime container from Netflix/vmaf
mkvhelper dependency update      # rebuild only if Netflix tagged a newer release
mkvhelper dependency remove      # delete every built image + clone + state file
```

On first `compress` run with no container present, the build is kicked off
automatically (~10–20 minutes; image is reused on subsequent runs).

## Subcommand details

### `compress`

VMAF-guided AV1 NVENC encode.  Accepts a folder of inputs (any container
ffmpeg can read), classifies each by source type, applies the right
restoration filter, then runs a CQ ladder search until the encode hits the
configured VMAF thresholds (mean ≥ 97, p05 ≥ 95, p01 ≥ 90 by default).
Per-file output dir contains the final encode plus a `decisions.log` and
`log.json` for after-the-fact inspection.  See [VMAF on GPU](#vmaf-on-gpu)
below for how the libvmaf_cuda path is plugged in.

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

## VMAF on GPU

CPU libvmaf dominates the wall-clock cost of CQ tuning.  The `compress`
workflow ships a containerised CUDA-enabled FFmpeg build (libvmaf_cuda) per
the upstream
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

All `compress` build artifacts live under `~/.local/share/mkvhelper/`
(XDG_DATA_HOME):

```
mkvhelper/
├── state.json              # Built upstream tag, image tag, source path
├── vmaf/<tag>/             # Shallow clone of Netflix/vmaf at the built tag
└── build-logs/<tag>.log    # Captured stdout+stderr from podman build
```

The container images themselves live in Podman's storage
(`~/.local/share/containers/`) and are referenced by tag — `dependency
remove` cleans both.

## Dependencies

- **.NET 10 SDK** (every project targets `net10.0`).
- **`mkvextract` and `mkvmerge`** (MKVToolNix) for `split` and `print-chapters`.
  Arch: `sudo pacman -S mkvtoolnix-cli`.
- **For container `compress` (recommended)**: `podman` and the NVIDIA
  Container Toolkit.  Arch:
  ```bash
  sudo pacman -S podman nvidia-container-toolkit
  sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml
  ```
- **For native `compress` fallback (`--no-container`)**: `ffmpeg` and
  `ffprobe` on `$PATH`, with `libvmaf`, `libzimg`, `idet`, `fieldmatch`,
  `bwdif`, and `decimate`.
- **NVIDIA GPU with NVENC + NVDEC** for production `compress` runs (tests
  do not require it).
