using System.Diagnostics;
using Supprocom.FFmpeg;
using Xabe.FFmpeg;

FFmpegBinaries.Validate();
string expectedRuntimeIdentifier = Environment.GetEnvironmentVariable("SUPPROCOM_TEST_RID") ?? "linux-x64";
if (!FFmpegBinaries.RuntimeIdentifier.Equals(expectedRuntimeIdentifier, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected payload Runtime Identifier '{FFmpegBinaries.RuntimeIdentifier}'.");
}

string? path = Environment.GetEnvironmentVariable("PATH");
Environment.SetEnvironmentVariable(
    "PATH",
    string.IsNullOrEmpty(path)
        ? FFmpegBinaries.Directory
        : FFmpegBinaries.Directory + Path.PathSeparator + path);
string input = Path.Combine(Path.GetTempPath(), $"supprocom-xabe-{Guid.NewGuid():N}.mkv");
try
{
    GenerateMedia(input);
    IMediaInfo mediaInfo = await Xabe.FFmpeg.FFmpeg.GetMediaInfo(input);
    if (mediaInfo.Duration <= TimeSpan.Zero || mediaInfo.VideoStreams.Count() != 1)
    {
        throw new InvalidOperationException("Xabe.FFmpeg did not return the expected video analysis.");
    }

    Console.WriteLine($"Xabe.FFmpeg validated {mediaInfo.Duration.TotalMilliseconds:0} ms through '{FFmpegBinaries.Directory}'.");
}
finally
{
    File.Delete(input);
}

static void GenerateMedia(string output)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = FFmpegBinaries.FFmpegPath,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (string argument in new[]
             {
                 "-nostdin", "-hide_banner", "-loglevel", "error",
                 "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=5",
                 "-t", "0.4", "-c:v", "ffv1", "-y", output
             })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Unable to start FFmpeg for the wrapper fixture.");
    string error = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(30_000) || process.ExitCode != 0)
    {
        throw new InvalidOperationException($"FFmpeg fixture generation failed: {error}");
    }
}
