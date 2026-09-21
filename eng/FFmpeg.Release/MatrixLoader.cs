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
        ValidatePackageLocks(matrix, Path.GetDirectoryName(Path.GetFullPath(path))!);
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
        if (matrix.SchemaVersion != 4)
        {
            throw new ReleaseFailureException("UnsupportedMatrixSchema", "Only release-matrix schema version 4 is supported.");
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

        string[] expectedFlavors = ["full", "lgpl"];
        if (!expectedFlavors.SequenceEqual(matrix.Flavors.Select(item => item.Name), StringComparer.Ordinal))
        {
            throw new ReleaseFailureException(
                "UnexpectedLicenseFamily",
                "The release matrix must contain the full GPL family followed by the LGPL-only family.");
        }

        foreach (FlavorDefinition flavor in matrix.Flavors)
        {
            string[] commonConfigureArguments =
            [
                "--enable-libaom",
                "--enable-libopenh264",
                "--enable-libvpx",
                "--enable-libmp3lame",
                "--enable-libopus",
                "--enable-libass",
                "--enable-libsrt",
                "--enable-libssh"
            ];
            string[] commonEncoders = ["libaom-av1", "libopenh264", "libvpx-vp9", "libmp3lame", "libopus"];
            if (flavor.ConfigureArguments.Any(argument =>
                    argument.Equals("--enable-nonfree", StringComparison.OrdinalIgnoreCase)) ||
                !flavor.ConfigureArguments.Contains("--disable-nonfree", StringComparer.Ordinal) ||
                !flavor.ConfigureArguments.Contains("--enable-shared", StringComparer.Ordinal) ||
                !flavor.ConfigureArguments.Contains("--disable-static", StringComparer.Ordinal) ||
                !flavor.IncludeFfplay ||
                !flavor.ConfigureArguments.Contains("--enable-ffplay", StringComparer.Ordinal) ||
                commonConfigureArguments.Except(flavor.ConfigureArguments, StringComparer.Ordinal).Any() ||
                commonEncoders.Except(flavor.RequiredEncoders, StringComparer.Ordinal).Any() ||
                !flavor.RequiredProtocols.Contains("https", StringComparer.Ordinal) ||
                !flavor.RequiredProtocols.Contains("sftp", StringComparer.Ordinal) ||
                !flavor.RequiredProtocols.Contains("srt", StringComparer.Ordinal) ||
                flavor.RequiredEncoders.Count == 0 ||
                flavor.RequiredDecoders.Count == 0 ||
                flavor.RequiredFilters.Count == 0 ||
                flavor.RequiredProtocols.Count == 0)
            {
                throw new ReleaseFailureException(
                    "InvalidFlavorConfiguration",
                    $"Release flavor '{flavor.Name}' is incomplete, non-redistributable, or not a shared-library build.");
            }

            if (new[]
                {
                    flavor.RequiredEncoders,
                    flavor.RequiredDecoders,
                    flavor.RequiredFilters,
                    flavor.RequiredProtocols
                }.Any(values => values.Count != values.Distinct(StringComparer.Ordinal).Count()))
            {
                throw new ReleaseFailureException(
                    "DuplicateFlavorCapability",
                    $"Release flavor '{flavor.Name}' contains a duplicate required capability.");
            }
        }

        FlavorDefinition full = matrix.Flavors.Single(item => item.Name.Equals("full", StringComparison.Ordinal));
        string[] fullArguments =
        [
            "--enable-libx264",
            "--enable-libx265",
            "--enable-libxvid",
            "--enable-librubberband",
            "--enable-libvidstab"
        ];
        string[] fullEncoders = ["libx264", "libx265", "libxvid"];
        if (!full.LicenseExpression.Equals("GPL-3.0-or-later", StringComparison.Ordinal) ||
            !full.ConfigureArguments.Contains("--enable-gpl", StringComparer.Ordinal) ||
            !full.ConfigureArguments.Contains("--enable-version3", StringComparer.Ordinal) ||
            full.ConfigureArguments.Contains("--disable-gpl", StringComparer.Ordinal) ||
            fullArguments.Except(full.ConfigureArguments, StringComparer.Ordinal).Any() ||
            fullEncoders.Except(full.RequiredEncoders, StringComparer.Ordinal).Any() ||
            !full.RequiredFilters.Contains("rubberband", StringComparer.Ordinal) ||
            !full.RequiredFilters.Contains("vidstabdetect", StringComparer.Ordinal) ||
            !full.RequiredFilters.Contains("vidstabtransform", StringComparer.Ordinal))
        {
            throw new ReleaseFailureException(
                "InvalidFullConfiguration",
                "The full family must explicitly enable GPLv3-compatible code and disable nonfree code.");
        }

        FlavorDefinition lgpl = matrix.Flavors.Single(item => item.Name.Equals("lgpl", StringComparison.Ordinal));
        if (!lgpl.LicenseExpression.Equals("LGPL-3.0-or-later", StringComparison.Ordinal) ||
            lgpl.ConfigureArguments.Contains("--enable-gpl", StringComparer.Ordinal) ||
            !lgpl.ConfigureArguments.Contains("--disable-gpl", StringComparer.Ordinal) ||
            !lgpl.ConfigureArguments.Contains("--enable-version3", StringComparer.Ordinal) ||
            fullArguments.Intersect(lgpl.ConfigureArguments, StringComparer.Ordinal).Any() ||
            fullEncoders.Intersect(lgpl.RequiredEncoders, StringComparer.Ordinal).Any())
        {
            throw new ReleaseFailureException(
                "InvalidLgplConfiguration",
                "The LGPL family must explicitly disable GPL/nonfree code and use the LGPLv3 boundary.");
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

        ReleaseDefinition[] approvedReleases = matrix.Releases
            .Where(item => item.Status.Equals("approved", StringComparison.Ordinal))
            .ToArray();
        if (approvedReleases.Length != 2 || approvedReleases.Count(item => item.Default) != 1 ||
            approvedReleases.Select(item => item.PackageVersion).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            approvedReleases.Length)
        {
            throw new ReleaseFailureException(
                "InvalidApprovedReleases",
                "Exactly two unique approved package releases and one default release are required.");
        }

        foreach (ReleaseDefinition release in approvedReleases)
        {
            if (!approved.Any(item => item.Version.Equals(release.SourceVersion, StringComparison.Ordinal)) ||
                !matrix.Flavors.Any(item => item.Name.Equals(release.Flavor, StringComparison.Ordinal)))
            {
                throw new ReleaseFailureException(
                    "InvalidApprovedRelease",
                    $"Package release '{release.PackageVersion}' refers to an unapproved source or flavor.");
            }
        }

        ReleaseDefinition stableFull = approvedReleases.Single(item => item.Flavor.Equals("full", StringComparison.Ordinal));
        ReleaseDefinition prereleaseLgpl = approvedReleases.Single(item => item.Flavor.Equals("lgpl", StringComparison.Ordinal));
        if (!stableFull.PackageVersion.Equals("9.0.2.1", StringComparison.Ordinal) ||
            !stableFull.Default ||
            !prereleaseLgpl.PackageVersion.Equals("9.0.2-lgpl.1", StringComparison.Ordinal) ||
            prereleaseLgpl.Default)
        {
            throw new ReleaseFailureException(
                "InvalidPackageVersionContract",
                "The correction releases must be full 9.0.2.1 (default) and LGPL-only 9.0.2-lgpl.1.");
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
        string[] windowsSystemDependencies =
        [
            "ADVAPI32.dll",
            "AVICAP32.dll",
            "bcrypt.dll",
            "CFGMGR32.dll",
            "COMDLG32.dll",
            "CRYPT32.dll",
            "D3D11.dll",
            "D3D12.dll",
            "DINPUT8.dll",
            "DSOUND.dll",
            "DXGI.dll",
            "dxva2.dll",
            "GDI32.dll",
            "IMM32.dll",
            "IPHLPAPI.dll",
            "KERNEL32.dll",
            "MF.dll",
            "MFPlat.dll",
            "MFReadWrite.dll",
            "NORMALIZ.dll",
            "NTDLL.dll",
            "ole32.dll",
            "OLEAUT32.dll",
            "POWRPROF.dll",
            "PROPSYS.dll",
            "secur32.dll",
            "SETUPAPI.dll",
            "SHELL32.dll",
            "SHLWAPI.dll",
            "USER32.dll",
            "USERENV.dll",
            "VERSION.dll",
            "WINMM.dll",
            "WLDAP32.dll",
            "WS2_32.dll"
        ];
        string[] windowsUcrtDependencies =
        [
            "api-ms-win-crt-conio-l1-1-0.dll",
            "api-ms-win-crt-convert-l1-1-0.dll",
            "api-ms-win-crt-environment-l1-1-0.dll",
            "api-ms-win-crt-filesystem-l1-1-0.dll",
            "api-ms-win-crt-heap-l1-1-0.dll",
            "api-ms-win-crt-locale-l1-1-0.dll",
            "api-ms-win-crt-math-l1-1-0.dll",
            "api-ms-win-crt-private-l1-1-0.dll",
            "api-ms-win-crt-runtime-l1-1-0.dll",
            "api-ms-win-crt-stdio-l1-1-0.dll",
            "api-ms-win-crt-string-l1-1-0.dll",
            "api-ms-win-crt-time-l1-1-0.dll",
            "api-ms-win-crt-utility-l1-1-0.dll"
        ];
        string[] glibcDependencies = ["libc.so.6", "libdl.so.2", "libm.so.6", "libpthread.so.0", "librt.so.1"];
        string[] macDependencies =
        [
            "/usr/lib/libbz2.1.0.dylib",
            "/usr/lib/libc++.1.dylib",
            "/usr/lib/libiconv.2.dylib",
            "/usr/lib/libobjc.A.dylib",
            "/usr/lib/libresolv.9.dylib",
            "/usr/lib/libSystem.B.dylib",
            "/usr/lib/libz.1.dylib",
            "/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit",
            "/System/Library/Frameworks/AudioToolbox.framework/Versions/A/AudioToolbox",
            "/System/Library/Frameworks/AVFoundation.framework/Versions/A/AVFoundation",
            "/System/Library/Frameworks/CoreAudio.framework/Versions/A/CoreAudio",
            "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation",
            "/System/Library/Frameworks/CoreGraphics.framework/Versions/A/CoreGraphics",
            "/System/Library/Frameworks/CoreImage.framework/Versions/A/CoreImage",
            "/System/Library/Frameworks/CoreMedia.framework/Versions/A/CoreMedia",
            "/System/Library/Frameworks/CoreServices.framework/Versions/A/CoreServices",
            "/System/Library/Frameworks/CoreText.framework/Versions/A/CoreText",
            "/System/Library/Frameworks/CoreVideo.framework/Versions/A/CoreVideo",
            "/System/Library/Frameworks/Foundation.framework/Versions/C/Foundation",
            "/System/Library/Frameworks/IOKit.framework/Versions/A/IOKit",
            "/System/Library/Frameworks/IOSurface.framework/Versions/A/IOSurface",
            "/System/Library/Frameworks/Metal.framework/Versions/A/Metal",
            "/System/Library/Frameworks/QuartzCore.framework/Versions/A/QuartzCore",
            "/System/Library/Frameworks/Security.framework/Versions/A/Security",
            "/System/Library/Frameworks/VideoToolbox.framework/Versions/A/VideoToolbox"
        ];
        foreach (RuntimeDefinition runtime in runtimes)
        {
            switch (runtime.Os)
            {
                case "windows":
                    string windowsFloor = runtime.Architecture == "arm64" ? "10.0.26200" : "10.0.26100";
                    IReadOnlyList<string> windowsDependencies = runtime.Architecture == "x86"
                        ? [.. windowsSystemDependencies, "msvcrt.dll"]
                        : [.. windowsUcrtDependencies, .. windowsSystemDependencies];
                    RequireRuntimePolicy(
                        runtime,
                        windowsFloor,
                        libc: null,
                        minimumLibcVersion: null,
                        workerImage: null,
                        windowsDependencies);
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

            ValidateCpuAndToolchainPolicy(runtime);
        }
    }

    private static void ValidateCpuAndToolchainPolicy(RuntimeDefinition runtime)
    {
        string expectedCpuBaseline = runtime.Rid switch
        {
            "win-x86" => "x86-i686-sse2",
            "win-x64" or "linux-x64" or "linux-musl-x64" or "osx-x64" => "x86-64-v1",
            "osx-arm64" => "apple-m1",
            "win-arm64" or "linux-arm64" or "linux-musl-arm64" => "armv8-a",
            _ => string.Empty
        };
        ToolchainDefinition? toolchain = runtime.Toolchain;
        string expectedPackageManager = runtime.Os switch
        {
            "windows" => "pacman",
            "linux" => "apt",
            "linux-musl" => "apk",
            "macos" => "homebrew",
            _ => string.Empty
        };
        bool immutableSnapshot = toolchain is not null &&
            (string.Equals(toolchain.RepositorySnapshot, runtime.WorkerImage, StringComparison.Ordinal) ||
             (toolchain.RepositorySnapshot?.StartsWith(
                 "https://github.com/actions/runner-images/commit/",
                 StringComparison.Ordinal) ?? false));
        if (!string.Equals(runtime.CpuBaseline, expectedCpuBaseline, StringComparison.Ordinal) ||
            toolchain is null ||
            string.IsNullOrWhiteSpace(toolchain.EnvironmentIdentity) ||
            !immutableSnapshot ||
            !string.Equals(toolchain.PackageManager, expectedPackageManager, StringComparison.Ordinal) ||
            toolchain.Packages.Count == 0 ||
            toolchain.MediaPackages is null ||
            toolchain.MediaPackages.Count == 0 ||
            toolchain.FullPackages is null ||
            toolchain.FullPackages.Count == 0 ||
            toolchain.Packages.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) ||
            toolchain.MediaPackages.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) ||
            toolchain.FullPackages.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)))
        {
            throw InvalidRuntimePolicy(runtime);
        }

        if (runtime.Rid.Equals("win-x86", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(toolchain.PackageLockSha256))
            {
                throw InvalidRuntimePolicy(runtime);
            }

            ValidateHex(toolchain.PackageLockSha256, 64, "win-x86 package-lock SHA-256");
        }
        else if (toolchain.PackageLockSha256 is not null)
        {
            throw InvalidRuntimePolicy(runtime);
        }
    }

    private static void ValidatePackageLocks(ReleaseMatrix matrix, string matrixDirectory)
    {
        RuntimeDefinition runtime = matrix.RuntimeIdentifiers.Single(item =>
            item.Rid.Equals("win-x86", StringComparison.Ordinal));
        string lockPath = Path.Combine(matrixDirectory, "msys2-i686-packages.lock");
        if (!File.Exists(lockPath))
        {
            throw new ReleaseFailureException(
                "PackageLockMissing",
                "The SHA-locked MSYS2 i686 media package archive list is missing.");
        }

        byte[] bytes = File.ReadAllBytes(lockPath);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!actualHash.Equals(runtime.Toolchain.PackageLockSha256, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException(
                "PackageLockHashMismatch",
                "The MSYS2 i686 media package lock does not match its approved SHA-256.");
        }

        var lockedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(lockPath))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length != 4 ||
                !fields[0].StartsWith("mingw-w64-i686-", StringComparison.Ordinal) ||
                fields[1].Length == 0 ||
                fields[2].Length == 0 ||
                Path.GetFileName(fields[2]) != fields[2] ||
                !fields[2].EndsWith(".pkg.tar.zst", StringComparison.Ordinal) ||
                !lockedVersions.TryAdd(fields[0], fields[1]))
            {
                throw new ReleaseFailureException(
                    "PackageLockInvalid",
                    "The MSYS2 i686 media package lock contains an invalid or duplicate entry.");
            }

            ValidateHex(fields[3], 64, "MSYS2 i686 package SHA-256");
        }

        IEnumerable<KeyValuePair<string, string>> requiredPackages =
            runtime.Toolchain.MediaPackages!.Concat(runtime.Toolchain.FullPackages!);
        foreach ((string package, string version) in requiredPackages)
        {
            if (!lockedVersions.TryGetValue(package, out string? lockedVersion) ||
                !lockedVersion.Equals(version, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException(
                    "PackageLockIncomplete",
                    $"The MSYS2 i686 package lock does not pin required package '{package}={version}'.");
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
