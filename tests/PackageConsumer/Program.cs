using System.Diagnostics;
using Supprocom.FFmpeg;

FFmpegBinaries.Validate();
string expectedRuntimeIdentifier = Environment.GetEnvironmentVariable("SUPPROCOM_TEST_RID") ?? "linux-x64";
if (!FFmpegBinaries.RuntimeIdentifier.Equals(expectedRuntimeIdentifier, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected payload Runtime Identifier '{FFmpegBinaries.RuntimeIdentifier}'.");
}

string ffmpegVersion = Run(FFmpegBinaries.FFmpegPath, "-version");
string ffprobeVersion = Run(FFmpegBinaries.FFprobePath, "-version");
if (ffmpegVersion.IndexOf("ffmpeg version 9.0.2", StringComparison.Ordinal) < 0 ||
    ffprobeVersion.IndexOf("ffprobe version 9.0.2", StringComparison.Ordinal) < 0)
{
    throw new InvalidOperationException("The deployed tools do not report FFmpeg 9.0.2.");
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
