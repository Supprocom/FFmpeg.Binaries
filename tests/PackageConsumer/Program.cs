using System.Diagnostics;
using Supprocom.FFmpeg;

FFmpegBinaries.Validate();
string expectedRuntimeIdentifier = Environment.GetEnvironmentVariable("SUPPROCOM_TEST_RID") ?? "linux-x64";
string expectedFlavor = Environment.GetEnvironmentVariable("SUPPROCOM_TEST_FLAVOR") ?? "full";
string expectedPackageVersion = Environment.GetEnvironmentVariable("SUPPROCOM_TEST_PACKAGE_VERSION") ?? "9.0.2.1";
if (!FFmpegBinaries.RuntimeIdentifier.Equals(expectedRuntimeIdentifier, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected payload Runtime Identifier '{FFmpegBinaries.RuntimeIdentifier}'.");
}

if (!FFmpegBinaries.Version.StartsWith(expectedPackageVersion, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected managed package version '{FFmpegBinaries.Version}'.");
}

string ffmpegVersion = Run(FFmpegBinaries.FFmpegPath, "-version");
string ffprobeVersion = Run(FFmpegBinaries.FFprobePath, "-version");
string ffplayVersion = Run(FFmpegBinaries.FFplayPath, "-version");
if (ffmpegVersion.IndexOf("ffmpeg version 9.0.2", StringComparison.Ordinal) < 0 ||
    ffprobeVersion.IndexOf("ffprobe version 9.0.2", StringComparison.Ordinal) < 0 ||
    ffplayVersion.IndexOf("ffplay version 9.0.2", StringComparison.Ordinal) < 0)
{
    throw new InvalidOperationException("The deployed FFmpeg, FFprobe, and FFplay tools do not report FFmpeg 9.0.2.");
}

string encoders = Run(FFmpegBinaries.FFmpegPath, "-hide_banner -encoders");
string[] commonEncoders = ["libaom-av1", "libopenh264", "libvpx-vp9"];
if (commonEncoders.Any(encoder => encoders.IndexOf(encoder, StringComparison.Ordinal) < 0))
{
    throw new InvalidOperationException("The deployed FFmpeg payload is missing a required redistributable encoder.");
}

string[] gplEncoders = ["libx264", "libx265", "libxvid"];
bool exposesEveryGplEncoder = gplEncoders.All(encoder => encoders.IndexOf(encoder, StringComparison.Ordinal) >= 0);
if ((expectedFlavor == "full") != exposesEveryGplEncoder)
{
    throw new InvalidOperationException("The deployed FFmpeg payload does not match its declared full/LGPL feature boundary.");
}

Console.WriteLine($"Validated {FFmpegBinaries.RuntimeIdentifier} in '{FFmpegBinaries.Directory}'.");

static string Run(string executable, string argument)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    startInfo.Arguments = argument;
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Unable to start '{executable}'.");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(30_000))
    {
        process.Kill();
        throw new InvalidOperationException($"'{executable}' timed out: {error}");
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"'{executable}' exited with code 0x{unchecked((uint)process.ExitCode):X8}: {error}");
    }

    return output;
}
