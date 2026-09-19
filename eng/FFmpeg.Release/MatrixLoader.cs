using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.FFmpeg.Release;

internal static class MatrixLoader
{
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
        return (matrix, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal static void Validate(ReleaseMatrix matrix)
    {
        if (matrix.SchemaVersion != 1)
        {
            throw new ReleaseFailureException("UnsupportedMatrixSchema", "Only release-matrix schema version 1 is supported.");
        }

        string[] actualRids = matrix.RuntimeIdentifiers.Select(item => item.Rid).ToArray();
        if (actualRids.Length != actualRids.Distinct(StringComparer.Ordinal).Count() ||
            !RequiredRuntimeIdentifiers.SetEquals(actualRids))
        {
            throw new ReleaseFailureException(
                "IncompleteRuntimeMatrix",
                "The stable release matrix must contain exactly the nine approved Runtime Identifiers.");
        }

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

    private static void ValidateHex(string value, int length, string label)
    {
        if (value.Length != length || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ReleaseFailureException("InvalidSourceEvidence", $"The {label} is not a valid {length}-digit hexadecimal value.");
        }
    }
}
