# Supprocom.FFmpeg.Binaries

Reference `Supprocom.FFmpeg.Binaries` once, then build or publish with a supported Runtime Identifier. The package copies the selected FFmpeg and FFprobe tools, their required shared libraries, notices, and build metadata to the application-local `ffmpeg` directory. It never changes the machine `PATH` and never downloads a runtime payload during build or application startup.

Raw process callers can use `Supprocom.FFmpeg.FFmpegBinaries.FFmpegPath` and `FFprobePath`. FFMpegCore can use the containing directory as its binary-folder setting. Xabe.FFmpeg can use the same directory through its executable-path configuration.

Supported Runtime Identifiers are `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`.

The default family is LGPL and does not include GPL or nonfree builds. Corresponding source is published separately as `Supprocom.FFmpeg.Source` at the exact same version.
