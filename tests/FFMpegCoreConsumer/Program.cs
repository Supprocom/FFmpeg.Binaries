using System.Diagnostics;
using FFMpegCore;
using Supprocom.FFmpeg;

FFmpegBinaries.Validate();
GlobalFFOptions.Configure(new FFOptions
{
    BinaryFolder = FFmpegBinaries.Directory,
    TemporaryFilesFolder = Path.GetTempPath()
});

string input = Path.Combine(Path.GetTempPath(), $"supprocom-ffmpegcore-{Guid.NewGuid():N}.mkv");
try
{
    GenerateMedia(input);
    IMediaAnalysis analysis = FFProbe.Analyse(input);
    if (analysis.Duration <= TimeSpan.Zero || analysis.PrimaryVideoStream is null)
    {
        throw new InvalidOperationException("FFMpegCore did not return the expected video analysis.");
    }

    Console.WriteLine($"FFMpegCore validated {analysis.Duration.TotalMilliseconds:0} ms through '{FFmpegBinaries.Directory}'.");
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
