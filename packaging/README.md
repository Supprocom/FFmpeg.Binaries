# Supprocom.FFmpeg.Binaries

Reference `Supprocom.FFmpeg.Binaries` once, then build or publish with a supported Runtime Identifier. The package copies the selected FFmpeg and FFprobe tools, their required shared libraries, notices, and build metadata to the fixed application-local `ffmpeg` directory. The runtime helper API resolves that same directory. `SupprocomFFmpegOutputDirectory` values other than `ffmpeg` are rejected at build time so deployment and runtime discovery cannot disagree. The package never downloads a runtime payload during build or application startup.

Raw process callers can use `Supprocom.FFmpeg.FFmpegBinaries.FFmpegPath` and `FFprobePath`. FFMpegCore can use the containing directory as its binary-folder setting. The tested Xabe.FFmpeg 6.0.2 integration prepends `FFmpegBinaries.Directory` to the process `PATH` before the first Xabe API call. That mutation is process-global, affects child-process lookup for the rest of the process unless restored, and should be serialized with any other `PATH` changes. No working Xabe explicit-directory integration is claimed.

Supported Runtime Identifiers are `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`.

The compatibility boundaries are explicit and enforced from every frozen native file:

| Runtime Identifiers | Minimum supported OS/ABI boundary | CPU baseline |
| --- | --- | --- |
| `win-x86` | Windows kernel 10.0.26100 (Windows Server 2025 boundary used for the native smoke test) | i686 with SSE2 |
| `win-x64` | Windows kernel 10.0.26100 (Windows Server 2025 boundary used for the native smoke test) | x86-64-v1 |
| `win-arm64` | Windows kernel 10.0.26200 (Windows 11 ARM64 boundary used for the native smoke test) | ARMv8-A |
| `linux-x64` | Ubuntu 24.04-compatible environment with glibc 2.39 | x86-64-v1 |
| `linux-arm64` | Ubuntu 24.04-compatible environment with glibc 2.39 | ARMv8-A |
| `linux-musl-x64` | Alpine 3.23-compatible environment with musl 1.2.5 | x86-64-v1 |
| `linux-musl-arm64` | Alpine 3.23-compatible environment with musl 1.2.5 | ARMv8-A |
| `osx-x64` | macOS 15.0 | x86-64-v1 |
| `osx-arm64` | macOS 15.0 on Apple silicon | Apple M1 |

Release gates inspect the architecture of every executable and shared library, require explicit compiler CPU targets with runtime CPU dispatch, and inspect the full native dependency closure against a per-RID system-library allowlist. Windows workers must exactly match the declared kernel floor, smoke-test there, and retain every PE OS/subsystem header and imported system DLL as evidence. The gates also reject glibc symbol requirements above the declared boundary, a mismatched ELF interpreter or Mach-O deployment target, non-application-local runtime search paths, and payloads that do not meet the platform hardening baseline.

The release matrix also locks each hosted-runner image or container digest and the exact build-package versions. Every worker rejects version drift before compiling and embeds the complete installed-package inventory, repository inputs, and compiler/assembler/linker/strip/patchelf identities in `BUILD-METADATA/toolchain-provenance.json`.

The default binary family is LGPL-configured and does not include GPL or nonfree components. Complete corresponding source is published separately as `Supprocom.FFmpeg.Source` at the exact same version; because that archive contains all upstream source files, its package metadata and notices describe the upstream mixed licensing rather than labeling the archive LGPL-only.
