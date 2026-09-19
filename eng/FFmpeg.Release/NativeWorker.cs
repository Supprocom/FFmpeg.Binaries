using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Supprocom.FFmpeg.Release;

internal sealed class NativeWorker(ProcessRunner processRunner)
{
    private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan FateTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] FateTests = ["fate-ffmpeg-filter_complex", "fate-filter-formats"];
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly Regex LoadAddressPattern = new(
        @"\(0x[0-9a-fA-F]+\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex LinuxSonamePattern = new(
        @"\.so\.\d+$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private const string FixedPrefix = "/opt/supprocom/ffmpeg";

    public async Task<string> BuildAsync(
        string repositoryRoot,
        ReleasePlan plan,
        string planHash,
        string planDirectory,
        VersionDefinition version,
        FlavorDefinition flavor,
        RuntimeDefinition runtime,
        SourceVerificationResult source,
        string? workerOutputRoot,
        CancellationToken cancellationToken)
    {
        ValidateWorkerHost(runtime);
        string workerRoot = Path.Combine(planDirectory, "workers", runtime.Rid);
        string comparisonRoot = Path.Combine(workerRoot, "independent-builds");
        string workRoot = Path.Combine(workerRoot, "work");
        RecreateOwnedDirectory(comparisonRoot, planDirectory);

        BuildOutcome first = await BuildOnceAsync(
            repositoryRoot,
            version,
            flavor,
            runtime,
            source,
            workRoot,
            Path.Combine(comparisonRoot, "first"),
            cancellationToken).ConfigureAwait(false);
        BuildOutcome second = await BuildOnceAsync(
            repositoryRoot,
            version,
            flavor,
            runtime,
            source,
            workRoot,
            Path.Combine(comparisonRoot, "second"),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkerFile> firstFiles = await InventoryAsync(first.PayloadDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WorkerFile> secondFiles = await InventoryAsync(second.PayloadDirectory, cancellationToken).ConfigureAwait(false);
        string difference = ExplainFirstDifference(firstFiles, secondFiles);
        if (difference.Length != 0)
        {
            throw new ReleaseFailureException(
                "NativeBuildNotReproducible",
                $"The two independent {runtime.Rid} builds differ: {difference}");
        }

        string acceptedRoot = Path.Combine(workerRoot, "accepted");
        RecreateOwnedDirectory(acceptedRoot, planDirectory);
        string acceptedPayload = Path.Combine(acceptedRoot, "payload");
        CopyTree(first.PayloadDirectory, acceptedPayload);
        string configureHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', first.ConfigureArguments))));
        var manifest = new WorkerManifest(
            1,
            planHash,
            plan.Version,
            plan.SourceCommit,
            plan.ArchiveSha256,
            flavor.Name,
            runtime.Rid,
            runtime.Worker,
            configureHash,
            first.ConfigureArguments,
            FateTests,
            first.ArchitectureEvidence,
            first.DynamicDependencyEvidence,
            true,
            true,
            firstFiles,
            DateTimeOffset.UtcNow);
        string manifestPath = Path.Combine(acceptedRoot, "worker-manifest.json");
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, ReleaseJsonContext.Default.WorkerManifest),
            cancellationToken).ConfigureAwait(false);
        string manifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(acceptedRoot, "worker-manifest.sha256"),
            manifestHash + "  worker-manifest.json\n",
            Encoding.ASCII,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workerOutputRoot))
        {
            ExportAcceptedWorker(repositoryRoot, workerOutputRoot, runtime.Rid, acceptedRoot);
        }

        RecreateOwnedDirectory(workRoot, planDirectory);
        Directory.Delete(workRoot);
        return manifestPath;
    }

    private async Task<BuildOutcome> BuildOnceAsync(
        string repositoryRoot,
        VersionDefinition version,
        FlavorDefinition flavor,
        RuntimeDefinition runtime,
        SourceVerificationResult source,
        string workRoot,
        string resultRoot,
        CancellationToken cancellationToken)
    {
        RecreateOwnedDirectory(workRoot, Path.GetDirectoryName(workRoot)!);
        RecreateOwnedDirectory(resultRoot, Path.GetDirectoryName(resultRoot)!);
        string sourceRoot = Path.Combine(workRoot, "source");
        string stageRoot = Path.Combine(workRoot, "stage");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(stageRoot);
        CommandResult extract = await processRunner.RunAsync(
            "tar",
            ["-xJf", source.ArchivePath, "--strip-components=1", "-C", sourceRoot],
            workRoot,
            TimeSpan.FromMinutes(3),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(extract, "SourceExtractionFailed");

        IReadOnlyList<string> configureArguments = CreateConfigureArguments(flavor, runtime);
        var deterministicEnvironment = new Dictionary<string, string?>
        {
            ["SOURCE_DATE_EPOCH"] = version.SourceDateEpoch!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TZ"] = "UTC",
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
            ["ZERO_AR_DATE"] = "1"
        };
        AddBuildRuntimeSearchPath(deterministicEnvironment, sourceRoot, runtime);
        (string configureTool, IReadOnlyList<string> arguments) = await ConfigureCommandAsync(
            sourceRoot,
            configureArguments,
            cancellationToken).ConfigureAwait(false);
        CommandResult configure = await processRunner.RunAsync(
            configureTool,
            arguments,
            sourceRoot,
            ConfigureTimeout,
            deterministicEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(configure, "FFmpegConfigureFailed");
        ValidateGeneratedConfiguration(sourceRoot);

        int parallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        CommandResult build = await processRunner.RunAsync(
            "make",
            ["-j", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            sourceRoot,
            BuildTimeout,
            deterministicEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(build, "FFmpegBuildFailed");

        CommandResult fate = await processRunner.RunAsync(
            "make",
            ["-j", "2", .. FateTests],
            sourceRoot,
            FateTimeout,
            deterministicEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(fate, "FFmpegFateFailed");

        string installStage = await ToToolPathAsync(stageRoot, sourceRoot, cancellationToken).ConfigureAwait(false);
        CommandResult install = await processRunner.RunAsync(
            "make",
            [$"DESTDIR={installStage}", "install"],
            sourceRoot,
            TimeSpan.FromMinutes(5),
            deterministicEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(install, "FFmpegInstallFailed");

        string installedBin = Path.Combine(stageRoot, "opt", "supprocom", "ffmpeg", "bin");
        if (!Directory.Exists(installedBin))
        {
            throw new ReleaseFailureException("FFmpegInstallLayoutInvalid", "The native build did not produce the fixed application-local bin directory.");
        }

        NormalizeLinks(installedBin);
        PruneRedundantLibraryAliases(installedBin, runtime);
        await NormalizeRuntimeSearchPathAsync(installedBin, runtime, cancellationToken).ConfigureAwait(false);
        await StripBinariesAsync(
            installedBin,
            runtime,
            version.SourceDateEpoch.Value,
            cancellationToken).ConfigureAwait(false);
        string payloadRoot = Path.Combine(resultRoot, "payload");
        CopyTree(installedBin, payloadRoot);
        AddComplianceFiles(repositoryRoot, sourceRoot, payloadRoot, version, flavor, runtime, configureArguments);
        ValidateExpectedPayload(payloadRoot, runtime);
        string architectureEvidence = await InspectArchitectureAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string dependencyEvidence = await InspectDynamicDependenciesAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        NormalizePayloadPermissions(payloadRoot);
        RejectAbsoluteBuildPath(payloadRoot, workRoot);
        await RunSmokeTestAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        return new BuildOutcome(payloadRoot, configureArguments, architectureEvidence, dependencyEvidence);
    }

    private static List<string> CreateConfigureArguments(FlavorDefinition flavor, RuntimeDefinition runtime)
    {
        var arguments = new List<string>
        {
            $"--prefix={FixedPrefix}",
            $"--bindir={FixedPrefix}/bin",
            $"--shlibdir={FixedPrefix}/bin",
            $"--libdir={FixedPrefix}/lib",
            $"--incdir={FixedPrefix}/include",
            "--disable-doc",
            "--disable-debug",
            "--disable-autodetect",
            "--enable-pic",
            "--enable-network",
            "--extra-version=Supprocom"
        };
        arguments.AddRange(flavor.ConfigureArguments);
        switch (runtime.Os)
        {
            case "linux":
            case "linux-musl":
                arguments.Add("--target-os=linux");
                arguments.Add($"--arch={ToConfigureArchitecture(runtime.Architecture)}");
                arguments.Add("--enable-pthreads");
                arguments.Add("--cc=gcc");
                break;
            case "macos":
                arguments.Add("--target-os=darwin");
                arguments.Add($"--arch={ToConfigureArchitecture(runtime.Architecture)}");
                arguments.Add("--enable-pthreads");
                arguments.Add("--cc=clang");
                arguments.Add("--install-name-dir=@rpath");
                arguments.Add("--extra-ldflags=-Wl,-rpath,@loader_path");
                break;
            case "windows":
                arguments.Add("--target-os=mingw32");
                arguments.Add($"--arch={ToConfigureArchitecture(runtime.Architecture)}");
                arguments.Add("--enable-w32threads");
                if (runtime.Architecture == "arm64")
                {
                    arguments.Add("--cc=clang");
                    arguments.Add("--cxx=clang++");
                    arguments.Add("--ar=llvm-ar");
                    arguments.Add("--nm=llvm-nm");
                    arguments.Add("--ranlib=llvm-ranlib");
                    arguments.Add("--strip=llvm-strip");
                }
                else
                {
                    arguments.Add("--cc=gcc");
                }
                break;
            default:
                throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is not implemented.");
        }

        return arguments;
    }

    private async Task<(string Tool, IReadOnlyList<string> Arguments)> ConfigureCommandAsync(
        string sourceRoot,
        IReadOnlyList<string> configureArguments,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            CommandResult shellPath = await processRunner.RunAsync(
                "cygpath",
                ["-w", "/usr/bin/bash"],
                sourceRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(shellPath, "WorkerShellResolutionFailed");
            string bash = shellPath.StandardOutput.Trim();
            if (!File.Exists(bash))
            {
                throw new ReleaseFailureException(
                    "WorkerShellResolutionFailed",
                    "The MSYS2 Bash executable could not be resolved to a Windows path.");
            }

            return (bash, ["./configure", .. configureArguments]);
        }

        return (Path.Combine(sourceRoot, "configure"), configureArguments);
    }

    private async Task<string> ToToolPathAsync(string path, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        CommandResult result = await processRunner.RunAsync(
            "cygpath",
            ["-u", path],
            workingDirectory,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "WorkerPathConversionFailed");
        return result.StandardOutput.Trim();
    }

    private static void ValidateGeneratedConfiguration(string sourceRoot)
    {
        HashSet<string> config = File.ReadLines(Path.Combine(sourceRoot, "ffbuild", "config.mak"))
            .ToHashSet(StringComparer.Ordinal);
        if (config.Contains("CONFIG_NONFREE=yes") ||
            config.Contains("CONFIG_GPL=yes") ||
            !config.Contains("CONFIG_SHARED=yes") ||
            config.Contains("CONFIG_STATIC=yes"))
        {
            throw new ReleaseFailureException(
                "GeneratedLicenseConfigurationInvalid",
                "The generated FFmpeg configuration crossed the approved LGPL/shared-library boundary.");
        }
    }

    private async Task NormalizeRuntimeSearchPathAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Os is not ("linux" or "linux-musl"))
        {
            return;
        }

        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime))
        {
            CommandResult result = await processRunner.RunAsync(
                "patchelf",
                ["--set-rpath", "$ORIGIN", path],
                payloadRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "RuntimeSearchPathPatchFailed");
        }
    }

    private async Task StripBinariesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        long sourceDateEpoch,
        CancellationToken cancellationToken)
    {
        var deterministicEnvironment = new Dictionary<string, string?>
        {
            ["SOURCE_DATE_EPOCH"] = sourceDateEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime))
        {
            IReadOnlyList<string> arguments = runtime.Os == "macos"
                ? ["-x", path]
                : ["--strip-unneeded", path];
            CommandResult result = await processRunner.RunAsync(
                "strip",
                arguments,
                payloadRoot,
                TimeSpan.FromSeconds(60),
                deterministicEnvironment,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "BinaryStripFailed");
        }
    }

    private static IEnumerable<string> EnumerateNativeFiles(string payloadRoot, RuntimeDefinition runtime)
    {
        string executableSuffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        foreach (string path in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (name.Equals("ffmpeg" + executableSuffix, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ffprobe" + executableSuffix, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".so", StringComparison.Ordinal) ||
                name.EndsWith(".dylib", StringComparison.Ordinal) ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static void NormalizeLinks(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is null)
            {
                continue;
            }

            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !File.Exists(target.FullName))
            {
                throw new ReleaseFailureException("InvalidPayloadSymlink", $"Native payload link '{info.Name}' has no regular-file target.");
            }

            if (OperatingSystem.IsWindows())
            {
                File.Delete(path);
                File.Copy(target.FullName, path, overwrite: false);
            }
            else
            {
                UnixFileMode mode = File.GetUnixFileMode(target.FullName);
                File.Delete(path);
                File.Copy(target.FullName, path, overwrite: false);
                File.SetUnixFileMode(path, mode);
            }
        }
    }

    private static void PruneRedundantLibraryAliases(string directory, RuntimeDefinition runtime)
    {
        Regex? sonamePattern = runtime.Os switch
        {
            "linux" or "linux-musl" => LinuxSonamePattern,
            "macos" => null,
            _ => null
        };
        if (sonamePattern is null && runtime.Os != "macos")
        {
            return;
        }

        string[] libraries = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => runtime.Os == "macos"
                ? path.EndsWith(".dylib", StringComparison.Ordinal)
                : Path.GetFileName(path).Contains(".so", StringComparison.Ordinal))
            .ToArray();
        foreach (IGrouping<string, string> group in libraries.GroupBy(path => LibraryStem(Path.GetFileName(path), runtime.Os)))
        {
            string? soname = group.SingleOrDefault(path => runtime.Os == "macos"
                ? IsMacSoname(Path.GetFileName(path), group.Key)
                : sonamePattern!.IsMatch(Path.GetFileName(path)));
            if (soname is null)
            {
                throw new ReleaseFailureException("SharedLibrarySonameMissing", $"Shared library '{group.Key}' has no major-version SONAME file.");
            }

            foreach (string redundant in group.Where(path => !path.Equals(soname, StringComparison.Ordinal)))
            {
                File.Delete(redundant);
            }
        }
    }

    internal static bool IsMacSoname(string fileName, string libraryStem)
    {
        string prefix = libraryStem + ".";
        const string suffix = ".dylib";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        int versionLength = fileName.Length - prefix.Length - suffix.Length;
        if (versionLength <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> version = fileName.AsSpan(prefix.Length, versionLength);
        return int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static string LibraryStem(string fileName, string operatingSystem)
    {
        int marker = operatingSystem == "macos"
            ? fileName.IndexOf('.', StringComparison.Ordinal)
            : fileName.IndexOf(".so", StringComparison.Ordinal);
        return marker < 0 ? fileName : fileName[..marker];
    }

    private static void AddComplianceFiles(
        string repositoryRoot,
        string sourceRoot,
        string payloadRoot,
        VersionDefinition version,
        FlavorDefinition flavor,
        RuntimeDefinition runtime,
        IReadOnlyList<string> configureArguments)
    {
        File.WriteAllText(Path.Combine(payloadRoot, ".rid"), runtime.Rid + "\n", new UTF8Encoding(false));
        string licenses = Path.Combine(payloadRoot, "licenses");
        Directory.CreateDirectory(licenses);
        foreach (string name in new[] { "COPYING.LGPLv2.1", "COPYING.LGPLv3", "LICENSE.md" })
        {
            File.Copy(Path.Combine(sourceRoot, name), Path.Combine(licenses, name), overwrite: false);
        }

        string notices = Path.Combine(payloadRoot, "notices");
        Directory.CreateDirectory(notices);
        File.WriteAllText(
            Path.Combine(notices, "THIRD-PARTY-NOTICES.txt"),
            "This initial hermetic build uses FFmpeg's in-tree components and no optional external codec library.\n" +
            "Operating-system libraries listed in BUILD-METADATA/dynamic-dependencies.txt remain host components.\n",
            new UTF8Encoding(false));
        string metadata = Path.Combine(payloadRoot, "BUILD-METADATA");
        Directory.CreateDirectory(metadata);
        WriteSanitizedBuildFile(
            Path.Combine(sourceRoot, "ffbuild", "config.mak"),
            Path.Combine(metadata, "config.mak"),
            sourceRoot);
        WriteSanitizedBuildFile(
            Path.Combine(sourceRoot, "config.h"),
            Path.Combine(metadata, "config.h"),
            sourceRoot);
        File.WriteAllLines(Path.Combine(metadata, "configure-arguments.txt"), configureArguments, new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(metadata, "source.json"),
            JsonSerializer.Serialize(
                new
                {
                    version = version.Version,
                    version.Tag,
                    version.SourceCommit,
                    version.ArchiveSha256,
                    version.ArchiveUri,
                    flavor = flavor.Name,
                    runtimeIdentifier = runtime.Rid,
                    buildRepository = "https://github.com/Supprocom/FFmpeg.Binaries"
                },
                IndentedJson) + "\n",
            new UTF8Encoding(false));
        File.Copy(
            Path.Combine(repositoryRoot, "eng", "release-matrix.json"),
            Path.Combine(metadata, "release-matrix.json"));
    }

    private static void ValidateExpectedPayload(string payloadRoot, RuntimeDefinition runtime)
    {
        string suffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        foreach (string name in new[] { "ffmpeg" + suffix, "ffprobe" + suffix })
        {
            string path = Path.Combine(payloadRoot, name);
            if (!File.Exists(path))
            {
                throw new ReleaseFailureException("RequiredToolMissing", $"The worker payload is missing '{name}'.");
            }

            if (!OperatingSystem.IsWindows())
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                {
                    throw new ReleaseFailureException("ExecutablePermissionMissing", $"The worker payload tool '{name}' is not executable.");
                }
            }
        }

        if (!EnumerateNativeFiles(payloadRoot, runtime).Any(path =>
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".so", StringComparison.Ordinal) ||
                path.EndsWith(".dylib", StringComparison.Ordinal)))
        {
            throw new ReleaseFailureException("SharedLibrariesMissing", "The LGPL worker payload contains no shared FFmpeg libraries.");
        }
    }

    private static void AddBuildRuntimeSearchPath(
        Dictionary<string, string?> environment,
        string sourceRoot,
        RuntimeDefinition runtime)
    {
        string[] libraryDirectories =
        [
            "libavdevice",
            "libavfilter",
            "libavformat",
            "libavcodec",
            "libswresample",
            "libswscale",
            "libavutil"
        ];
        string value = string.Join(
            Path.PathSeparator,
            libraryDirectories.Select(directory => Path.Combine(sourceRoot, directory)));
        if (runtime.Os is "linux" or "linux-musl")
        {
            environment["LD_LIBRARY_PATH"] = value;
        }
        else if (runtime.Os == "macos")
        {
            environment["DYLD_LIBRARY_PATH"] = value;
        }
        else if (runtime.Os == "windows")
        {
            environment["PATH"] = value + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
    }

    private static void WriteSanitizedBuildFile(string source, string destination, string sourceRoot)
    {
        string text = File.ReadAllText(source)
            .Replace(sourceRoot, "/usr/src/ffmpeg", StringComparison.Ordinal)
            .Replace(sourceRoot.Replace('\\', '/'), "/usr/src/ffmpeg", StringComparison.Ordinal);
        File.WriteAllText(destination, text, new UTF8Encoding(false));
    }

    private async Task<string> InspectArchitectureAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(payloadRoot, runtime.Os == "windows" ? "ffmpeg.exe" : "ffmpeg");
        CommandResult result = await processRunner.RunAsync(
            "file",
            ["--brief", executable],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "ArchitectureInspectionFailed");
        string evidence = result.StandardOutput.Trim();
        string[] expectedTokens = runtime.Architecture switch
        {
            "x86" => ["80386", "i386"],
            "x64" => ["x86-64", "x86_64"],
            "arm64" => ["aarch64", "arm64"],
            _ => throw new ReleaseFailureException("UnsupportedArchitecture", $"Architecture '{runtime.Architecture}' is unsupported.")
        };
        if (!expectedTokens.Any(token => evidence.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReleaseFailureException("ArchitectureMismatch", $"The payload architecture evidence does not match {runtime.Rid}.");
        }

        return evidence;
    }

    private async Task<string> InspectDynamicDependenciesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(payloadRoot, runtime.Os == "windows" ? "ffmpeg.exe" : "ffmpeg");
        (string tool, string[] arguments) = runtime.Os switch
        {
            "linux" or "linux-musl" => ("ldd", new[] { executable }),
            "macos" => ("otool", new[] { "-L", executable }),
            "windows" => ("objdump", new[] { "-p", executable }),
            _ => throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is unsupported.")
        };
        CommandResult result = await processRunner.RunAsync(
            tool,
            arguments,
            payloadRoot,
            TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "DynamicDependencyInspectionFailed");
        string evidence = runtime.Os == "windows"
            ? CanonicalizeWindowsDependencyEvidence(result.StandardOutput + result.StandardError)
            : CanonicalizeDependencyEvidence(
                (result.StandardOutput + result.StandardError).Trim(),
                payloadRoot);
        await File.WriteAllTextAsync(
            Path.Combine(payloadRoot, "BUILD-METADATA", "dynamic-dependencies.txt"),
            evidence + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    private static string CanonicalizeDependencyEvidence(string evidence, string payloadRoot)
    {
        string canonical = evidence
            .Replace(payloadRoot, "$PAYLOAD", StringComparison.Ordinal)
            .Replace(payloadRoot.Replace('\\', '/'), "$PAYLOAD", StringComparison.Ordinal);
        return LoadAddressPattern.Replace(canonical, "(address)");
    }

    internal static string CanonicalizeWindowsDependencyEvidence(string evidence)
    {
        string[] dependencies = evidence
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("DLL Name:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["DLL Name:".Length..].Trim())
            .Where(name => name.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (dependencies.Length == 0)
        {
            throw new ReleaseFailureException(
                "DynamicDependencyInspectionInvalid",
                "The Windows dependency inspection reported no imported DLLs.");
        }

        return string.Join('\n', dependencies.Select(name => $"DLL Name: {name}"));
    }

    private async Task RunSmokeTestAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string suffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        string ffmpeg = Path.Combine(payloadRoot, "ffmpeg" + suffix);
        string ffprobe = Path.Combine(payloadRoot, "ffprobe" + suffix);
        string output = Path.Combine(payloadRoot, "smoke-output.mkv");
        var environment = new Dictionary<string, string?>();
        if (runtime.Os is "linux" or "linux-musl")
        {
            environment["LD_LIBRARY_PATH"] = payloadRoot;
        }
        else if (runtime.Os == "macos")
        {
            environment["DYLD_LIBRARY_PATH"] = payloadRoot;
        }

        CommandResult generate = await processRunner.RunAsync(
            ffmpeg,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                "-t", "1", "-c:v", "ffv1", "-c:a", "pcm_s16le", "-y", output
            ],
            payloadRoot,
            SmokeTimeout,
            environment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(generate, "FFmpegSmokeGenerationFailed");
        CommandResult inspect = await processRunner.RunAsync(
            ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", output],
            payloadRoot,
            SmokeTimeout,
            environment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(inspect, "FFprobeSmokeInspectionFailed");
        using JsonDocument document = JsonDocument.Parse(inspect.StandardOutput);
        if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) ||
            streams.GetArrayLength() != 2 ||
            !document.RootElement.TryGetProperty("format", out _))
        {
            throw new ReleaseFailureException("SmokeMediaInvalid", "FFprobe did not report the expected two-stream smoke-test media.");
        }

        File.Delete(output);
    }

    private static void RejectAbsoluteBuildPath(string payloadRoot, string buildRoot)
    {
        byte[] marker = Encoding.UTF8.GetBytes(buildRoot);
        foreach (string path in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().IndexOf(marker) >= 0)
            {
                throw new ReleaseFailureException("AbsoluteBuildPathLeak", $"Payload file '{Path.GetFileName(path)}' embeds the worker build root.");
            }
        }
    }

    private static async Task<IReadOnlyList<WorkerFile>> InventoryAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var files = new List<WorkerFile>();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            string mode = OperatingSystem.IsWindows()
                ? "windows"
                : Convert.ToString((int)File.GetUnixFileMode(path), 8).PadLeft(4, '0');
            files.Add(new WorkerFile(
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                info.Length,
                await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
                mode));
        }

        return files;
    }

    private static string ExplainFirstDifference(
        IReadOnlyList<WorkerFile> first,
        IReadOnlyList<WorkerFile> second)
    {
        if (first.Count != second.Count)
        {
            return $"file count {first.Count} versus {second.Count}";
        }

        for (int index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
            {
                return $"'{first[index].Path}' differs: " +
                    $"first length {first[index].Size}, SHA-256 {first[index].Sha256}; " +
                    $"second length {second[index].Size}, SHA-256 {second[index].Sha256}";
            }
        }

        return string.Empty;
    }

    private static void NormalizePayloadPermissions(string payloadRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode readOnlyData =
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        const UnixFileMode executable =
            readOnlyData |
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(payloadRoot, executable);
        foreach (string directory in Directory.EnumerateDirectories(payloadRoot, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(directory, executable);
        }

        foreach (string file in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
        {
            bool isNative = Path.GetFileName(file) is "ffmpeg" or "ffprobe" ||
                Path.GetFileName(file).Contains(".so", StringComparison.Ordinal) ||
                file.EndsWith(".dylib", StringComparison.Ordinal);
            File.SetUnixFileMode(file, isNative ? executable : readOnlyData);
        }
    }

    private static void ExportAcceptedWorker(
        string repositoryRoot,
        string configuredRoot,
        string runtimeIdentifier,
        string acceptedRoot)
    {
        string exportRoot = Path.GetFullPath(configuredRoot);
        string rootPath = Path.GetPathRoot(exportRoot) ?? string.Empty;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
        {
            throw new ReleaseFailureException("UnsafeWorkerExportPath", "The worker export root cannot be a filesystem root.");
        }

        string repositoryPrefix = Path.GetFullPath(repositoryRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(repositoryPrefix, comparison))
        {
            throw new ReleaseFailureException("WorkerExportInsideRepository", "Worker artifacts must be exported outside the source checkout.");
        }

        Directory.CreateDirectory(exportRoot);
        string destination = Path.Combine(exportRoot, runtimeIdentifier);
        RecreateOwnedDirectory(destination, exportRoot);
        CopyTree(acceptedRoot, destination);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
    }

    private static void RecreateOwnedDirectory(string path, string ownerRoot)
    {
        string fullPath = Path.GetFullPath(path);
        string fullOwner = Path.GetFullPath(ownerRoot) + Path.DirectorySeparatorChar;
        if (!(fullPath + Path.DirectorySeparatorChar).StartsWith(fullOwner, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("UnsafeWorkerPath", "A worker directory escaped its plan-owned root.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }

        Directory.CreateDirectory(fullPath);
    }

    private static void ValidateWorkerHost(RuntimeDefinition runtime)
    {
        bool matchesOs = runtime.Os switch
        {
            "windows" => OperatingSystem.IsWindows(),
            "macos" => OperatingSystem.IsMacOS(),
            "linux" or "linux-musl" => OperatingSystem.IsLinux(),
            _ => false
        };
        if (!matchesOs)
        {
            throw new ReleaseFailureException("WorkerHostMismatch", $"Runtime Identifier '{runtime.Rid}' cannot be built on this host OS.");
        }

        if (runtime.Os == "linux-musl" && !File.Exists("/etc/alpine-release"))
        {
            throw new ReleaseFailureException("WorkerLibcMismatch", $"Runtime Identifier '{runtime.Rid}' requires a musl/Alpine worker.");
        }

        if (runtime.Os == "linux" && File.Exists("/etc/alpine-release"))
        {
            throw new ReleaseFailureException("WorkerLibcMismatch", $"Runtime Identifier '{runtime.Rid}' requires a glibc worker.");
        }

        Architecture processArchitecture = RuntimeInformation.ProcessArchitecture;
        bool matchesArchitecture = runtime.Architecture switch
        {
            "x86" => OperatingSystem.IsWindows() &&
                (processArchitecture == Architecture.X86 || processArchitecture == Architecture.X64),
            "x64" => processArchitecture == Architecture.X64,
            "arm64" => processArchitecture == Architecture.Arm64,
            _ => false
        };
        if (!matchesArchitecture)
        {
            throw new ReleaseFailureException(
                "WorkerArchitectureMismatch",
                $"Runtime Identifier '{runtime.Rid}' cannot run its smoke test on {processArchitecture}.");
        }
    }

    private static string ToConfigureArchitecture(string architecture) => architecture switch
    {
        "x86" => "x86",
        "x64" => "x86_64",
        "arm64" => "aarch64",
        _ => throw new ReleaseFailureException("UnsupportedArchitecture", $"Architecture '{architecture}' is unsupported.")
    };

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void EnsureSuccess(CommandResult result, string code)
    {
        if (result.ExitCode != 0)
        {
            string diagnostic = string.Join(
                '\n',
                (result.StandardError + "\n" + result.StandardOutput)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .TakeLast(20));
            throw new ReleaseFailureException(code, $"A required native-worker command failed.\n{diagnostic}");
        }
    }

    private sealed record BuildOutcome(
        string PayloadDirectory,
        IReadOnlyList<string> ConfigureArguments,
        string ArchitectureEvidence,
        string DynamicDependencyEvidence);
}
