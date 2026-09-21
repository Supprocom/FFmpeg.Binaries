using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Supprocom.FFmpeg;

/// <summary>Provides stable paths to the application-local FFmpeg tools.</summary>
public static class FFmpegBinaries
{
    private const int ExecuteAccess = 1;

    /// <summary>Gets the application-local FFmpeg deployment directory.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    /// <summary>Gets the absolute path to the FFmpeg executable.</summary>
    public static string FFmpegPath => Path.Combine(Directory, IsWindows ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>Gets the absolute path to the FFprobe executable.</summary>
    public static string FFprobePath => Path.Combine(Directory, IsWindows ? "ffprobe.exe" : "ffprobe");

    /// <summary>Gets the absolute path to the FFplay executable.</summary>
    public static string FFplayPath => Path.Combine(Directory, IsWindows ? "ffplay.exe" : "ffplay");

    /// <summary>Gets the FFmpeg package version.</summary>
    public static string Version =>
        typeof(FFmpegBinaries).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FFmpegBinaries).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Gets the runtime identifier recorded with the deployed payload, when available.</summary>
    public static string RuntimeIdentifier
    {
        get
        {
            string path = Path.Combine(Directory, "runtime-identifier.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
    }

    /// <summary>Checks that all three tools exist and are executable on the current platform.</summary>
    /// <exception cref="FileNotFoundException">A required tool is absent.</exception>
    /// <exception cref="UnauthorizedAccessException">A Unix tool lacks execute permission.</exception>
    public static void Validate()
    {
        ValidateTool(FFmpegPath);
        ValidateTool(FFprobePath);
        ValidateTool(FFplayPath);
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static void ValidateTool(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required application-local FFmpeg tool is missing.", path);
        }

        if (!IsWindows && Access(path, ExecuteAccess) != 0)
        {
            throw new UnauthorizedAccessException($"The application-local tool '{path}' is not executable.");
        }
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string pathname, int mode);
}
