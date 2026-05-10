# CompressMKV

VMAF-guided AV1 NVENC compressor for arbitrary MKV files. Detects whether the
source is progressive, telecined, interlaced, or a mix; restores the original
cadence; then searches for the highest-CQ encode that hits a per-frame VMAF
quality target.

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
├── CompressMKV.CLI/               Main app (executable)
│   ├── Detection/                 §7.2.2 classification (idet → 5 categories)
│   ├── Restore/                   §7.2.3 action selection + filter chains
│   ├── Encoding/                  ffmpeg pipeline + AV1 NVENC encode
│   ├── Tuning/                    VMAF-guided CQ selection
│   ├── Infrastructure/            ffprobe, Fps, Proc utilities
│   ├── Models/                    summary/report DTOs
│   ├── Config.cs                  Runtime configuration
│   └── Program.cs                 Entry point
└── CompressMKV.Tests/             NUnit test suite
```

## Build & test

```bash
dotnet build
dotnet test                                   # all tests (~3s)
dotnet test --filter "Category!=Integration"  # unit tests only (<100ms)
dotnet test --filter "Category=Integration"   # ffmpeg integration tests
```

The test suite is fully self-contained: every test clip is generated on the
fly via ffmpeg's `lavfi` source filter, so a fresh `git clone` + `dotnet test`
runs to green with no external test data.

## Run

```bash
dotnet run --project CompressMKV.CLI -- \
    --input  /path/to/input/folder \
    --output /path/to/output/folder
```

## Dependencies

- .NET 10 SDK
- `ffmpeg` and `ffprobe` on `$PATH`, with `libvmaf`, `libzimg`, `idet`,
  `fieldmatch`, `bwdif`, and `decimate` (any modern build)
- NVIDIA GPU with NVENC + NVDEC for production runs (tests do not require it)
- VMAF models for production runs (Arch Linux: `pacman -S vmaf`, models land
  in `/usr/share/model/`)
