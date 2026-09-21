# Supprocom.FFmpeg.Binaries

Reference `Supprocom.FFmpeg.Binaries` once, then build or publish with a supported Runtime Identifier. The package deploys FFmpeg, FFprobe, FFplay, the FFmpeg shared libraries, every non-system shared-library dependency, notices, and build metadata to the fixed application-local `ffmpeg` directory. Nothing is downloaded during build or application startup.

Two package families are published from the same exact FFmpeg 9.0.2 source:

| Package version | Configuration | License boundary |
| --- | --- | --- |
| `9.0.2.1` | Full redistributable build | GPL-3.0-or-later |
| `9.0.2-lgpl.1` | LGPL-only build | LGPL-3.0-or-later |

The full `9.0.2.1` family enables GPL and version-3 code and includes the locked redistributable media stack. Its enforced cross-platform encoder contract includes x264 H.264, x265 HEVC, Xvid, OpenH264, libaom AV1, libvpx VP8/VP9, MP3 LAME, Opus, Vorbis, Theora, Speex, OpenJPEG, and WebP. It also enforces ASS/subtitle rendering, drawtext, Rubber Band, vid.stab, zscale, HTTPS, SFTP, and SRT. Platform-native acceleration and capture/output integrations are enabled where FFmpeg supports them. Nonfree FFmpeg components are never enabled.

The `9.0.2-lgpl.1` family uses the same package IDs and deployment contract but explicitly disables GPL and nonfree code. It retains OpenH264, libaom AV1, libvpx VP8/VP9, MP3 LAME, Opus, Vorbis, Theora, Speex, OpenJPEG, WebP, subtitles, HTTPS, SFTP, and SRT; it rejects x264, x265, Xvid, Rubber Band, and vid.stab during the frozen-binary feature gate. Codec availability does not grant patent rights; in particular, Cisco's binary patent-license terms do not apply to these independently built OpenH264 binaries.

Raw process callers can use `Supprocom.FFmpeg.FFmpegBinaries.FFmpegPath`, `FFprobePath`, and `FFplayPath`. FFMpegCore can use the containing directory as its binary-folder setting. The tested Xabe.FFmpeg 6.0.2 integration prepends `FFmpegBinaries.Directory` to the process `PATH` before the first Xabe API call. That mutation is process-global, affects child-process lookup until restored, and should be serialized with any other `PATH` changes. No working Xabe explicit-directory integration is claimed.

`SupprocomFFmpegOutputDirectory` values other than `ffmpeg` are rejected at build time so deployment and runtime discovery cannot disagree.

Supported Runtime Identifiers are `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`.

| Runtime Identifiers | Minimum supported OS/ABI boundary | CPU baseline |
| --- | --- | --- |
| `win-x86` | Windows kernel 10.0.26100 (Windows Server 2025 test boundary) | i686 with SSE2 |
| `win-x64` | Windows kernel 10.0.26100 (Windows Server 2025 test boundary) | x86-64-v1 |
| `win-arm64` | Windows kernel 10.0.26200 (Windows 11 ARM64 test boundary) | ARMv8-A |
| `linux-x64` | Ubuntu 24.04-compatible environment with glibc 2.39 | x86-64-v1 |
| `linux-arm64` | Ubuntu 24.04-compatible environment with glibc 2.39 | ARMv8-A |
| `linux-musl-x64` | Alpine 3.23-compatible environment with musl 1.2.5 | x86-64-v1 |
| `linux-musl-arm64` | Alpine 3.23-compatible environment with musl 1.2.5 | ARMv8-A |
| `osx-x64` | macOS 15.0 | x86-64-v1 |
| `osx-arm64` | macOS 15.0 on Apple silicon | Apple M1 |

Every worker performs two independent builds, compares the complete payload byte-for-byte, and then inspects every executable and shared library. Gates enforce the declared CPU target and runtime dispatch, OS/ABI boundary, complete native dependency closure, application-local search paths, platform hardening, codec/filter/protocol inventory, and real encode/decode smoke tests.

The release matrix locks each hosted-runner image or container digest and exact direct build-package versions. The deprecated win-x86 repository is replaced by a committed per-archive URL/version/SHA-256 lock. Every payload retains the complete installed-package inventory, repository inputs, compiler/assembler/linker/strip identities, configure state, feature inventories, and dependency evidence.

Corresponding source and build provenance are published as `Supprocom.FFmpeg.Source` at the exact same package version. That package's license file describes the mixed licensing of the complete source corpus; binary-package license expressions remain scoped to the selected full or LGPL-only build.
