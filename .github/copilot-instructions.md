# Project Guidelines

## Overview

CompressMKV is a CLI tool that batch-compresses MKV video files using NVIDIA NVENC AV1 hardware encoding with VMAF-guided quality tuning and automatic content-type detection (progressive, telecined, interlaced, mixed). Zero NuGet dependencies — external tools (ffmpeg, ffprobe) are invoked as child processes.

## Code Style

- **Namespace:** All files use `namespace CompressMkv;` (file-scoped, lowercase 'v')
- **Static logic classes:** Business logic lives in `public static class` types (e.g. `Proc`, `Pipelines`, `ContentDetector`, `VmafTuner`)
- **Sealed data classes:** All data/model types are `public sealed class` with `{ get; set; }` properties and default initializers (`= ""`, `= new()`) — no records
- **Enums** get XML doc comments per member
- **Switch expressions** for all enum-to-value mappings (see `RestoreFilters.For()`, `RestoreStrategyMapper.ContentTypeToMode()`)
- **Source-generated regex** via `[GeneratedRegex]` on `partial class` — see [Detection/ContentDetector.cs](src/CompressMKV/Detection/ContentDetector.cs)
- **Collection expressions** (`["-hwaccel", "cuda", ...]`) preferred over `new List<string>`
- **Nullable reference types** enabled; properties typed `string?` where appropriate
- Naming: `PascalCase` types/methods/properties, `camelCase` locals, `_camelCase` private fields

## Architecture

```
Detection/   → Content-type analysis via ffmpeg idet filter (ContentDetector, SourceClassifier)
Encoding/    → GPU-gated NVENC AV1 encode pipelines and VMAF measurement (FinalEncoder, GpuGate, Pipelines)
Infrastructure/ → Process execution, ffprobe, JSON I/O (Proc, Ffprobe, JsonIO)
Models/      → Top-level result types (OverallSummary, VideoSummary, RunError)
Restore/     → Map detection → ffmpeg filter chains + lossless previews (RestoreStrategyMapper, RestoreFilters)
Tuning/      → VMAF-guided CQ selection via random sample windows (VmafTuner, Sampler, Selector)
```

**Data flow per video** (orchestrated by `Program.ProcessOneAsync`):
Ffprobe → SourceClassifier → ContentDetector → RestoreStrategyMapper → [PreviewGenerator] → VmafTuner → FinalEncoder → JsonIO

## Build and Test

```bash
dotnet build src/CompressMKV/CompressMKV.csproj
dotnet run --project src/CompressMKV -- --input /path/to/mkvs --output /path/to/out --vmaf-model /path/to/vmaf_model.json
```

Target framework: `net10.0`. No test projects exist. No CI configuration.

## Project Conventions

- **No dependency injection.** `Config` is constructed in `Main()` and passed to static methods. `GpuGate` (semaphores) is the only stateful object.
- **Process execution** uses `Proc.RunAsync` (capture all output) and `Proc.RunStreamingAsync` (line-by-line callback). Always use `ProcessStartInfo.ArgumentList` — never string concatenation.
- **Error handling:** throw `InvalidOperationException` on failures; per-file `try/catch` in the main loop records errors as `RunError` in `OverallSummary`.
- **Async:** all I/O is `async Task<T>` with `CancellationToken` on every method. Parallelism via `Task.WhenAll` with `.Chunk(8)` batching. GPU access gated by `GpuGate.AcquireAsync()` returning `IDisposable`.
- **JSON:** `System.Text.Json` with `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`. Ffprobe models use `[JsonPropertyName]`.
- **Detection thresholds** in `ContentDetector` are `const double` — derived from signal physics, not tunable.

## Integration Points

- **ffmpeg/ffprobe** invoked via `Proc` — paths configurable in `Config` (default: `"ffmpeg"`, `"ffprobe"`)
- **VMAF model** file path required via `--vmaf-model` CLI argument
- **NVIDIA GPU** required: NVENC + NVDEC, configured for RTX 5080 (2 NVENC + 2 NVDEC slots in `GpuGate`)
- Codec: `av1_nvenc`, preset `p7`, VBR rate control, `p010le` pixel format, CUDA hwaccel
