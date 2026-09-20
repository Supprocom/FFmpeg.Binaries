# Supprocom.FFmpeg.Binaries

Reference `Supprocom.FFmpeg.Binaries` once, then build or publish with a supported Runtime Identifier. The package copies the selected FFmpeg and FFprobe tools, their required shared libraries, notices, and build metadata to the fixed application-local `ffmpeg` directory. The runtime helper API resolves that same directory. `SupprocomFFmpegOutputDirectory` values other than `ffmpeg` are rejected at build time so deployment and runtime discovery cannot disagree. The package never downloads a runtime payload during build or application startup.

Raw process callers can use `Supprocom.FFmpeg.FFmpegBinaries.FFmpegPath` and `FFprobePath`. FFMpegCore can use the containing directory as its binary-folder setting. The tested Xabe.FFmpeg 6.0.2 integration prepends `FFmpegBinaries.Directory` to the process `PATH` before the first Xabe API call. That mutation is process-global, affects child-process lookup for the rest of the process unless restored, and should be serialized with any other `PATH` changes. No working Xabe explicit-directory integration is claimed.

Supported Runtime Identifiers are `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`.

The Unix compatibility boundaries are explicit and enforced from every frozen native file:

| Runtime Identifiers | Minimum supported boundary |
| --- | --- |
| `linux-x64`, `linux-arm64` | Ubuntu 24.04-compatible environment with glibc 2.39 |
| `linux-musl-x64`, `linux-musl-arm64` | Alpine 3.23-compatible environment with musl 1.2.5 |
| `osx-x64`, `osx-arm64` | macOS 15.0 |

Release gates inspect the full native dependency closure against a per-RID system-library allowlist. They also reject glibc symbol requirements above the declared boundary, a mismatched ELF interpreter or Mach-O deployment target, non-application-local runtime search paths, and payloads that do not meet the platform hardening baseline.

The default binary family is LGPL-configured and does not include GPL or nonfree components. Complete corresponding source is published separately as `Supprocom.FFmpeg.Source` at the exact same version; because that archive contains all upstream source files, its package metadata and notices describe the upstream mixed licensing rather than labeling the archive LGPL-only.
