using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.FFmpeg.Release;

internal static class MatrixLoader
{
    private const string MuslWorkerImage =
        "mcr.microsoft.com/dotnet/sdk@sha256:3f9c03432d664163a90d20e3ed0a3784d0aa82c1b9cbd1a7dd4609fede95669e";

    private static readonly HashSet<string> RequiredRuntimeIdentifiers = new(StringComparer.Ordinal)
    {
        "win-x86",
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "linux-musl-arm64",
        "osx-x64",
        "osx-arm64"
    };

    public static (ReleaseMatrix Matrix, string Sha256) Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ReleaseMatrix matrix = JsonSerializer.Deserialize(bytes, ReleaseJsonContext.Default.ReleaseMatrix)
            ?? throw new ReleaseFailureException("InvalidReleaseMatrix", "The release matrix is empty.");
        Validate(matrix);
        return (matrix, ComputeCanonicalSha256(bytes));
    }

    internal static string ComputeCanonicalSha256(ReadOnlySpan<byte> bytes)
    {
        byte[] canonical = new byte[bytes.Length];
        int destination = 0;
        for (int source = 0; source < bytes.Length; source++)
        {
            byte value = bytes[source];
            if (value == '\r')
            {
                canonical[destination++] = (byte)'\n';
                if (source + 1 < bytes.Length && bytes[source + 1] == '\n')
                {
                    source++;
                }

                continue;
            }

            canonical[destination++] = value;
        }

        return Convert.ToHexStringLower(SHA256.HashData(canonical.AsSpan(0, destination)));
    }

    internal static void Validate(ReleaseMatrix matrix)
    {
        if (matrix.SchemaVersion != 2)
        {
            throw new ReleaseFailureException("UnsupportedMatrixSchema", "Only release-matrix schema version 2 is supported.");
        }

        string[] actualRids = matrix.RuntimeIdentifiers.Select(item => item.Rid).ToArray();
        if (actualRids.Length != actualRids.Distinct(StringComparer.Ordinal).Count() ||
            !RequiredRuntimeIdentifiers.SetEquals(actualRids))
        {
            throw new ReleaseFailureException(
                "IncompleteRuntimeMatrix",
                "The stable release matrix must contain exactly the nine approved Runtime Identifiers.");
        }

        ValidateRuntimePolicies(matrix.RuntimeIdentifiers);

        if (matrix.Flavors.Count != 1 || !matrix.Flavors[0].Name.Equals("lgpl", StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("UnexpectedLicenseFamily", "The default release matrix must contain only the LGPL family.");
        }

        FlavorDefinition flavor = matrix.Flavors[0];
        if (flavor.ConfigureArguments.Any(argument => argument.Equals("--enable-nonfree", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReleaseFailureException("NonfreeConfiguration", "--enable-nonfree is forbidden for every release family.");
        }

        if (flavor.ConfigureArguments.Any(argument => argument.Equals("--enable-gpl", StringComparison.OrdinalIgnoreCase)) ||
            !flavor.ConfigureArguments.Contains("--disable-gpl", StringComparer.Ordinal) ||
            !flavor.ConfigureArguments.Contains("--disable-nonfree", StringComparer.Ordinal) ||
            !flavor.ConfigureArguments.Contains("--enable-shared", StringComparer.Ordinal) ||
            !flavor.ConfigureArguments.Contains("--disable-static", StringComparer.Ordinal))
        {
            throw new ReleaseFailureException(
                "InvalidLgplConfiguration",
                "The LGPL family must explicitly disable GPL/nonfree code and use shared libraries.");
        }

        VersionDefinition[] approved = matrix.Versions.Where(item => item.Status.Equals("approved", StringComparison.Ordinal)).ToArray();
        if (approved.Length == 0)
        {
            throw new ReleaseFailureException("NoApprovedVersion", "The matrix does not contain an approved FFmpeg version.");
        }

        foreach (VersionDefinition version in approved)
        {
            if (new[]
                {
                    version.ArchiveUri,
                    version.SignatureUri,
                    version.ReleaseKeyUri,
                    version.ReleaseKeyFingerprint,
                    version.ArchiveSha256,
                    version.TagObject,
                    version.SourceCommit
                }.Any(string.IsNullOrWhiteSpace))
            {
                throw new ReleaseFailureException(
                    "IncompleteApprovedVersion",
                    $"Approved FFmpeg version '{version.Version}' lacks immutable source evidence.");
            }

            if (version.SourceDateEpoch is null or <= 0)
            {
                throw new ReleaseFailureException(
                    "IncompleteApprovedVersion",
                    $"Approved FFmpeg version '{version.Version}' lacks a valid source epoch.");
            }

            ValidateHex(version.ReleaseKeyFingerprint!, 40, "release key fingerprint");
            ValidateHex(version.ArchiveSha256!, 64, "archive SHA-256");
            ValidateHex(version.TagObject!, 40, "tag object");
            ValidateHex(version.SourceCommit!, 40, "source commit");
        }

        string[] expectedFeeds =
        [
            "https://api.nuget.org/v3/index.json",
            "https://nuget.pkg.github.com/Supprocom/index.json"
        ];
        if (!expectedFeeds.SequenceEqual(matrix.Feeds.Select(item => item.ServiceIndex), StringComparer.Ordinal))
        {
            throw new ReleaseFailureException("FeedMismatch", "The release feeds do not match the approved NuGet.org and Supprocom endpoints.");
        }
    }

    private static void ValidateRuntimePolicies(IReadOnlyList<RuntimeDefinition> runtimes)
    {
        string[] glibcDependencies = ["libc.so.6", "libdl.so.2", "libm.so.6", "libpthread.so.0", "librt.so.1"];
        string[] macDependencies =
        [
            "/usr/lib/libSystem.B.dylib",
            "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation",
            "/System/Library/Frameworks/CoreMedia.framework/Versions/A/CoreMedia",
            "/System/Library/Frameworks/CoreVideo.framework/Versions/A/CoreVideo"
        ];
        foreach (RuntimeDefinition runtime in runtimes)
        {
            switch (runtime.Os)
            {
                case "windows":
                    if (runtime.MinimumOsVersion is not null ||
                        runtime.Libc is not null ||
                        runtime.MinimumLibcVersion is not null ||
                        runtime.WorkerImage is not null ||
                        runtime.SystemDependencies is { Count: > 0 })
                    {
                        throw InvalidRuntimePolicy(runtime);
                    }

                    break;
                case "linux":
                    IReadOnlyList<string> approvedGlibcDependencies = runtime.Architecture == "arm64"
                        ? ["ld-linux-aarch64.so.1", .. glibcDependencies]
                        : glibcDependencies;
                    RequireRuntimePolicy(
                        runtime,
                        "24.04",
                        "glibc",
                        "2.39",
                        workerImage: null,
                        approvedGlibcDependencies);
                    break;
                case "linux-musl":
                    string muslDependency = runtime.Architecture switch
                    {
                        "x64" => "libc.musl-x86_64.so.1",
                        "arm64" => "libc.musl-aarch64.so.1",
                        _ => throw InvalidRuntimePolicy(runtime)
                    };
                    RequireRuntimePolicy(
                        runtime,
                        "3.23",
                        "musl",
                        "1.2.5",
                        MuslWorkerImage,
                        [muslDependency]);
                    break;
                case "macos":
                    RequireRuntimePolicy(
                        runtime,
                        "15.0",
                        libc: null,
                        minimumLibcVersion: null,
                        workerImage: null,
                        macDependencies);
                    break;
                default:
                    throw InvalidRuntimePolicy(runtime);
            }
        }
    }

    private static void RequireRuntimePolicy(
        RuntimeDefinition runtime,
        string minimumOsVersion,
        string? libc,
        string? minimumLibcVersion,
        string? workerImage,
        IReadOnlyList<string> systemDependencies)
    {
        IReadOnlyList<string>? actualDependencies = runtime.SystemDependencies;
        if (!string.Equals(runtime.MinimumOsVersion, minimumOsVersion, StringComparison.Ordinal) ||
            !string.Equals(runtime.Libc, libc, StringComparison.Ordinal) ||
            !string.Equals(runtime.MinimumLibcVersion, minimumLibcVersion, StringComparison.Ordinal) ||
            !string.Equals(runtime.WorkerImage, workerImage, StringComparison.Ordinal) ||
            actualDependencies is null ||
            actualDependencies.Count != actualDependencies.Distinct(StringComparer.Ordinal).Count() ||
            !systemDependencies.ToHashSet(StringComparer.Ordinal).SetEquals(actualDependencies))
        {
            throw InvalidRuntimePolicy(runtime);
        }
    }

    private static ReleaseFailureException InvalidRuntimePolicy(RuntimeDefinition runtime) =>
        new(
            "InvalidRuntimeCompatibilityPolicy",
            $"Runtime Identifier '{runtime.Rid}' does not declare the approved minimum OS/ABI and system-library allowlist.");

    private static void ValidateHex(string value, int length, string label)
    {
        if (value.Length != length || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ReleaseFailureException("InvalidSourceEvidence", $"The {label} is not a valid {length}-digit hexadecimal value.");
        }
    }
}
