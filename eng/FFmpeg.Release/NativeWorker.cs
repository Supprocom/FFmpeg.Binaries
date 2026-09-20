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
    private static readonly Regex LinuxSonamePattern = new(
        @"\.so\.\d+$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex ElfNeededPattern = new(
        @"Shared library: \[(?<name>[^\]]+)\]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex ElfRunPathPattern = new(
        @"Library r(?:un)?path: \[(?<path>[^\]]+)\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));
    private static readonly Regex GlibcVersionPattern = new(
        @"\bGLIBC_(?<version>\d+\.\d+(?:\.\d+)?)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MuslVersionPattern = new(
        @"\bVersion\s+(?<version>\d+\.\d+(?:\.\d+)?)\b",
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
        await ValidateBuildBaselineAsync(repositoryRoot, runtime, cancellationToken).ConfigureAwait(false);
        ToolchainProvenance toolchain = await new ToolchainInspector(processRunner).InspectAndValidateAsync(
            repositoryRoot,
            runtime,
            cancellationToken).ConfigureAwait(false);
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
            toolchain,
            workRoot,
            Path.Combine(comparisonRoot, "first"),
            cancellationToken).ConfigureAwait(false);
        BuildOutcome second = await BuildOnceAsync(
            repositoryRoot,
            version,
            flavor,
            runtime,
            source,
            toolchain,
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
        string toolchainHash = firstFiles.Single(file => file.Path.Equals(
            "BUILD-METADATA/toolchain-provenance.json",
            StringComparison.Ordinal)).Sha256;
        var manifest = new WorkerManifest(
            3,
            planHash,
            plan.Version,
            plan.SourceCommit,
            plan.ArchiveSha256,
            flavor.Name,
            runtime.Rid,
            runtime.Worker,
            runtime.CpuBaseline,
            toolchainHash,
            configureHash,
            first.ConfigureArguments,
            FateTests,
            first.ArchitectureEvidence,
            first.DynamicDependencyEvidence,
            first.AbiCompatibilityEvidence,
            first.HardeningEvidence,
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
        ToolchainProvenance toolchain,
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
        if (runtime.Os == "macos")
        {
            deterministicEnvironment["MACOSX_DEPLOYMENT_TARGET"] = runtime.MinimumOsVersion;
        }

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
        await BundleWindowsToolchainRuntimeAsync(installedBin, runtime, cancellationToken).ConfigureAwait(false);
        string payloadRoot = Path.Combine(resultRoot, "payload");
        CopyTree(installedBin, payloadRoot);
        AddComplianceFiles(
            repositoryRoot,
            sourceRoot,
            payloadRoot,
            version,
            flavor,
            runtime,
            toolchain,
            configureArguments);
        ValidateExpectedPayload(payloadRoot, runtime);
        string architectureEvidence = await InspectArchitectureAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string dependencyEvidence = await InspectDynamicDependenciesAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string abiEvidence = await InspectAbiCompatibilityAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string hardeningEvidence = await InspectHardeningAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        NormalizePayloadPermissions(payloadRoot);
        RejectAbsoluteBuildPath(payloadRoot, workRoot);
        await RunSmokeTestAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        return new BuildOutcome(
            payloadRoot,
            configureArguments,
            architectureEvidence,
            dependencyEvidence,
            abiEvidence,
            hardeningEvidence);
    }

    private static List<string> CreateConfigureArguments(FlavorDefinition flavor, RuntimeDefinition runtime)
    {
        (string configureCpu, string compilerFlags) = CpuTarget(runtime);
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
            "--extra-version=Supprocom",
            $"--cpu={configureCpu}"
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
                arguments.Add($"--extra-cflags={compilerFlags} -fstack-protector-strong");
                arguments.Add("--extra-ldflags=-Wl,-z,relro,-z,now");
                break;
            case "macos":
                arguments.Add("--target-os=darwin");
                arguments.Add($"--arch={ToConfigureArchitecture(runtime.Architecture)}");
                arguments.Add("--enable-pthreads");
                arguments.Add("--cc=clang");
                arguments.Add("--install-name-dir=@rpath");
                arguments.Add(
                    $"--extra-cflags={compilerFlags} -mmacosx-version-min={runtime.MinimumOsVersion} -fstack-protector-strong");
                arguments.Add($"--extra-ldflags=-Wl,-rpath,@loader_path -mmacosx-version-min={runtime.MinimumOsVersion}");
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

                arguments.Add($"--extra-cflags={compilerFlags}");
                break;
            default:
                throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is not implemented.");
        }

        return arguments;
    }

    private static (string ConfigureCpu, string CompilerFlags) CpuTarget(RuntimeDefinition runtime) =>
        runtime.CpuBaseline switch
        {
            "x86-i686-sse2" => ("i686", "-march=i686 -msse2 -mfpmath=sse"),
            "x86-64-v1" => ("x86-64", "-march=x86-64 -mtune=generic"),
            "armv8-a" => ("generic", "-march=armv8-a"),
            "apple-m1" => ("apple-m1", "-mcpu=apple-m1"),
            _ => throw new ReleaseFailureException(
                "UnsupportedCpuBaseline",
                $"CPU baseline '{runtime.CpuBaseline}' is unsupported for {runtime.Rid}.")
        };

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

    private async Task BundleWindowsToolchainRuntimeAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Os != "windows")
        {
            return;
        }

        var pending = new Queue<string>(
            EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.OrdinalIgnoreCase));
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out string? path))
        {
            if (!inspected.Add(path))
            {
                continue;
            }

            foreach (string dependency in await InspectWindowsDependenciesAsync(
                         path,
                         payloadRoot,
                         cancellationToken).ConfigureAwait(false))
            {
                if (!IsBundledWindowsToolchainRuntime(dependency))
                {
                    continue;
                }

                if (!Path.GetFileName(dependency).Equals(dependency, StringComparison.Ordinal))
                {
                    throw new ReleaseFailureException(
                        "WindowsRuntimeDependencyInvalid",
                        $"The imported Windows runtime dependency '{dependency}' is not a file name.");
                }

                string destination = Path.Combine(payloadRoot, dependency);
                if (File.Exists(destination))
                {
                    continue;
                }

                string source = await ResolveWindowsRuntimeLibraryAsync(
                    dependency,
                    payloadRoot,
                    cancellationToken).ConfigureAwait(false);
                File.Copy(source, destination, overwrite: false);
                CopyWindowsRuntimeLicenses(source, dependency, payloadRoot);
                pending.Enqueue(destination);
            }
        }
    }

    internal static bool IsBundledWindowsToolchainRuntime(string fileName) =>
        (fileName.StartsWith("libgcc_s_", StringComparison.OrdinalIgnoreCase) &&
         fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
        fileName.Equals("libwinpthread-1.dll", StringComparison.OrdinalIgnoreCase);

    private async Task<string> ResolveWindowsRuntimeLibraryAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string? mingwPrefix = Environment.GetEnvironmentVariable("MINGW_PREFIX");
        if (!string.IsNullOrWhiteSpace(mingwPrefix))
        {
            CommandResult converted = await processRunner.RunAsync(
                "cygpath",
                ["-w", $"{mingwPrefix.TrimEnd('/')}/bin/{fileName}"],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(converted, "WindowsRuntimeDependencyMissing");
            string resolved = converted.StandardOutput.Trim();
            if (File.Exists(resolved))
            {
                return resolved;
            }

            throw new ReleaseFailureException(
                "WindowsRuntimeDependencyMissing",
                $"The required Windows toolchain runtime '{fileName}' is missing from the active MSYS2 environment.");
        }

        CommandResult result = await processRunner.RunAsync(
            "where.exe",
            [fileName],
            workingDirectory,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "WindowsRuntimeDependencyMissing");
        string? path = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(File.Exists);
        return path ?? throw new ReleaseFailureException(
            "WindowsRuntimeDependencyMissing",
            $"The required Windows toolchain runtime '{fileName}' was not found on the worker PATH.");
    }

    private static void CopyWindowsRuntimeLicenses(string runtimePath, string fileName, string payloadRoot)
    {
        string binDirectory = Path.GetDirectoryName(runtimePath)!;
        string prefix = Directory.GetParent(binDirectory)?.FullName
            ?? throw new ReleaseFailureException(
                "WindowsRuntimeLicenseMissing",
                $"The installation prefix for '{fileName}' could not be resolved.");
        (string sourceName, string destinationName) = fileName.StartsWith(
            "libgcc_s_",
            StringComparison.OrdinalIgnoreCase)
            ? ("gcc-libs", "GCC-RUNTIME")
            : ("libwinpthread", "WINPTHREAD");
        string source = Path.Combine(prefix, "share", "licenses", sourceName);
        if (!Directory.Exists(source))
        {
            throw new ReleaseFailureException(
                "WindowsRuntimeLicenseMissing",
                $"The license directory for '{fileName}' is missing.");
        }

        string destination = Path.Combine(payloadRoot, "licenses", destinationName);
        if (Directory.Exists(destination))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        string[] licenseFiles = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (licenseFiles.Length == 0)
        {
            throw new ReleaseFailureException(
                "WindowsRuntimeLicenseMissing",
                $"The license directory for '{fileName}' is empty.");
        }

        foreach (string licenseFile in licenseFiles)
        {
            File.Copy(licenseFile, Path.Combine(destination, Path.GetFileName(licenseFile)), overwrite: false);
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
        ToolchainProvenance toolchain,
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
            "Windows toolchain runtime DLLs, when present, are accompanied by their license texts.\n" +
            "Operating-system DLLs in BUILD-METADATA/dynamic-dependencies.txt remain host components.\n",
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
        File.WriteAllBytes(
            Path.Combine(metadata, "toolchain-provenance.json"),
            JsonSerializer.SerializeToUtf8Bytes(toolchain, ReleaseJsonContext.Default.ToolchainProvenance));
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
                    runtime.CpuBaseline,
                    runtime.MinimumOsVersion,
                    runtime.Libc,
                    runtime.MinimumLibcVersion,
                    toolchainEnvironment = runtime.Toolchain.EnvironmentIdentity,
                    toolchainSnapshot = runtime.Toolchain.RepositorySnapshot,
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
        (string configureCpu, string compilerFlags) = CpuTarget(runtime);
        string generatedConfiguration = await File.ReadAllTextAsync(
            Path.Combine(payloadRoot, "BUILD-METADATA", "config.mak"),
            cancellationToken).ConfigureAwait(false);
        string configureArguments = await File.ReadAllTextAsync(
            Path.Combine(payloadRoot, "BUILD-METADATA", "configure-arguments.txt"),
            cancellationToken).ConfigureAwait(false);
        if (!generatedConfiguration.Contains("CONFIG_RUNTIME_CPUDETECT=yes", StringComparison.Ordinal) ||
            !configureArguments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Contains($"--cpu={configureCpu}", StringComparer.Ordinal) ||
            compilerFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(flag => !generatedConfiguration.Contains(flag, StringComparison.Ordinal)))
        {
            throw new ReleaseFailureException(
                "CpuBaselineConfigurationMismatch",
                $"The generated {runtime.Rid} configuration does not enforce CPU baseline '{runtime.CpuBaseline}' with runtime dispatch.");
        }

        string[] expectedTokens = runtime.Architecture switch
        {
            "x86" => ["80386", "i386"],
            "x64" => ["x86-64", "x86_64"],
            "arm64" => ["aarch64", "arm64"],
            _ => throw new ReleaseFailureException("UnsupportedArchitecture", $"Architecture '{runtime.Architecture}' is unsupported.")
        };
        var evidenceLines = new List<string>
        {
            $"cpuBaseline: {runtime.CpuBaseline}",
            $"configureCpu: {configureCpu}",
            $"compilerFlags: {compilerFlags}",
            "runtimeCpuDetection: enabled"
        };
        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.OrdinalIgnoreCase))
        {
            CommandResult result = await processRunner.RunAsync(
                "file",
                ["--brief", path],
                payloadRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "ArchitectureInspectionFailed");
            string evidence = result.StandardOutput.Trim();
            if (!expectedTokens.Any(token => evidence.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ReleaseFailureException(
                    "ArchitectureMismatch",
                    $"Native file '{Path.GetFileName(path)}' does not match {runtime.Rid} CPU baseline '{runtime.CpuBaseline}'.");
            }

            string formatRequirement = await InspectBinaryCpuRequirementAsync(
                path,
                payloadRoot,
                runtime,
                cancellationToken).ConfigureAwait(false);
            evidenceLines.Add($"{Path.GetFileName(path)}: {evidence}; {formatRequirement}");
        }

        return string.Join('\n', evidenceLines);
    }

    private async Task<string> InspectBinaryCpuRequirementAsync(
        string path,
        string workingDirectory,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Os == "windows")
        {
            CommandResult result = await processRunner.RunAsync(
                "objdump",
                ["-f", path],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "CpuBaselineInspectionFailed");
            string? architecture = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SingleOrDefault(line => line.StartsWith("architecture:", StringComparison.Ordinal));
            if (architecture is null)
            {
                throw new ReleaseFailureException(
                    "CpuBaselineInspectionFailed",
                    $"PE file '{Path.GetFileName(path)}' did not expose an architecture requirement.");
            }

            return architecture + "; compiler baseline and zero-optional-CPU smoke gate";
        }

        if (runtime.Os is "linux" or "linux-musl")
        {
            CommandResult result = await processRunner.RunAsync(
                "readelf",
                ["-W", "-h", "-n", path],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "CpuBaselineInspectionFailed");
            string[] lines = (result.StandardOutput + result.StandardError)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? machine = lines.SingleOrDefault(line => line.StartsWith("Machine:", StringComparison.Ordinal));
            string[] isaRequirements = lines
                .Where(line => line.Contains("x86 ISA needed:", StringComparison.Ordinal))
                .ToArray();
            if (machine is null ||
                isaRequirements.Any(line =>
                    line.Contains("x86-64-v2", StringComparison.Ordinal) ||
                    line.Contains("x86-64-v3", StringComparison.Ordinal) ||
                    line.Contains("x86-64-v4", StringComparison.Ordinal)))
            {
                throw new ReleaseFailureException(
                    "CpuBaselineExceeded",
                    $"ELF file '{Path.GetFileName(path)}' exceeds CPU baseline '{runtime.CpuBaseline}'.");
            }

            string isa = isaRequirements.Length == 0
                ? "no higher GNU ISA requirement"
                : string.Join("; ", isaRequirements);
            return machine + "; " + isa;
        }

        if (runtime.Os == "macos")
        {
            CommandResult result = await processRunner.RunAsync(
                "otool",
                ["-hv", path],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "CpuBaselineInspectionFailed");
            string? header = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line =>
                    line.Contains(runtime.Architecture == "x64" ? "X86_64" : "ARM64", StringComparison.Ordinal));
            if (header is null || !header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("ALL", StringComparer.Ordinal))
            {
                throw new ReleaseFailureException(
                    "CpuBaselineExceeded",
                    $"Mach-O file '{Path.GetFileName(path)}' does not use the baseline CPU subtype.");
            }

            return "Mach-O CPU subtype ALL; compiler baseline and zero-optional-CPU smoke gate";
        }

        throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is unsupported.");
    }

    private async Task<string> InspectDynamicDependenciesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Os == "windows")
        {
            HashSet<string> windowsBundledFiles = EnumerateNativeFiles(payloadRoot, runtime)
                .Select(path => Path.GetFileName(path)!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> windowsSystemDependencies = (runtime.SystemDependencies ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var windowsEvidenceLines = new List<string>();
            foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.OrdinalIgnoreCase))
            {
                IReadOnlyList<string> dependencies = await InspectWindowsDependenciesAsync(
                    path,
                    payloadRoot,
                    cancellationToken).ConfigureAwait(false);
                windowsEvidenceLines.Add(Path.GetFileName(path) + ":");
                foreach (string dependency in dependencies)
                {
                    if (windowsBundledFiles.Contains(dependency))
                    {
                        windowsEvidenceLines.Add("  bundled: " + dependency);
                    }
                    else if (windowsSystemDependencies.Contains(dependency))
                    {
                        windowsEvidenceLines.Add("  system: " + dependency);
                    }
                    else
                    {
                        throw new ReleaseFailureException(
                            "UndeclaredSystemDependency",
                            $"Native file '{Path.GetFileName(path)}' imports undeclared Windows system DLL '{dependency}'.");
                    }
                }
            }

            string windowsEvidence = string.Join('\n', windowsEvidenceLines);
            await File.WriteAllTextAsync(
                Path.Combine(payloadRoot, "BUILD-METADATA", "dynamic-dependencies.txt"),
                windowsEvidence + "\n",
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            return windowsEvidence;
        }

        HashSet<string> bundledFiles = EnumerateNativeFiles(payloadRoot, runtime)
            .Select(path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> allowedSystemDependencies = (runtime.SystemDependencies ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var evidenceLines = new List<string>();
        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal))
        {
            string[] dependencies;
            if (runtime.Os is "linux" or "linux-musl")
            {
                CommandResult result = await processRunner.RunAsync(
                    "readelf",
                    ["-W", "-d", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "DynamicDependencyInspectionFailed");
                dependencies = ParseElfDependencies(result.StandardOutput);
                Match runPath = ElfRunPathPattern.Match(result.StandardOutput);
                if (!runPath.Success || !runPath.Groups["path"].Value.Equals("$ORIGIN", StringComparison.Ordinal))
                {
                    throw new ReleaseFailureException(
                        "RuntimeSearchPathInvalid",
                        $"Native file '{Path.GetFileName(path)}' does not have the required application-local $ORIGIN runpath.");
                }
            }
            else if (runtime.Os == "macos")
            {
                CommandResult result = await processRunner.RunAsync(
                    "otool",
                    ["-L", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "DynamicDependencyInspectionFailed");
                dependencies = ParseMacDependencies(result.StandardOutput);
            }
            else
            {
                throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is unsupported.");
            }

            if (dependencies.Length == 0)
            {
                throw new ReleaseFailureException(
                    "DynamicDependencyInspectionInvalid",
                    $"The dependency inspection reported no native dependencies for '{Path.GetFileName(path)}'.");
            }

            evidenceLines.Add(Path.GetFileName(path) + ":");
            foreach (string dependency in dependencies)
            {
                string bundledName = runtime.Os == "macos"
                    ? Path.GetFileName(dependency)
                    : dependency;
                bool usesBundledFile = runtime.Os == "macos"
                    ? (dependency.StartsWith("@rpath/", StringComparison.Ordinal) ||
                       dependency.StartsWith("@loader_path/", StringComparison.Ordinal)) &&
                      bundledFiles.Contains(bundledName)
                    : bundledFiles.Contains(bundledName);
                if (usesBundledFile)
                {
                    evidenceLines.Add("  bundled: " + dependency);
                }
                else if (allowedSystemDependencies.Contains(dependency))
                {
                    evidenceLines.Add("  system: " + dependency);
                }
                else
                {
                    throw new ReleaseFailureException(
                        "UndeclaredSystemDependency",
                        $"Native file '{Path.GetFileName(path)}' depends on undeclared system library '{dependency}'.");
                }
            }
        }

        string evidence = string.Join('\n', evidenceLines);
        await File.WriteAllTextAsync(
            Path.Combine(payloadRoot, "BUILD-METADATA", "dynamic-dependencies.txt"),
            evidence + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    internal static string[] ParseElfDependencies(string evidence) =>
        ElfNeededPattern.Matches(evidence)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string[] ParseMacDependencies(string evidence) =>
        evidence
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(line =>
            {
                int metadata = line.IndexOf(" (", StringComparison.Ordinal);
                return metadata < 0 ? line : line[..metadata];
            })
            .Where(dependency => dependency.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private async Task<string> InspectAbiCompatibilityAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        var evidenceLines = new List<string>
        {
            $"runtimeIdentifier: {runtime.Rid}",
            $"cpuBaseline: {runtime.CpuBaseline}",
            $"minimumOsVersion: {runtime.MinimumOsVersion ?? "not-versioned"}",
            $"libc: {runtime.Libc ?? "not-applicable"}",
            $"minimumLibcVersion: {runtime.MinimumLibcVersion ?? "not-applicable"}"
        };
        if (runtime.Os == "windows")
        {
            Version actualWorker = Environment.OSVersion.Version;
            string actualWorkerBoundary = $"{actualWorker.Major}.{actualWorker.Minor}.{actualWorker.Build}";
            evidenceLines.Add("testedWindowsKernel: " + actualWorkerBoundary);
            int[] floor = ParseDottedVersion(runtime.MinimumOsVersion!);
            string peMaximum = $"{floor[0]}.{floor[1]}";
            foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.OrdinalIgnoreCase))
            {
                CommandResult result = await processRunner.RunAsync(
                    "objdump",
                    ["-p", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "AbiCompatibilityInspectionFailed");
                (string operatingSystemVersion, string subsystemVersion) = ParseWindowsPeVersions(result.StandardOutput);
                if (CompareDottedVersions(operatingSystemVersion, peMaximum) > 0 ||
                    CompareDottedVersions(subsystemVersion, peMaximum) > 0)
                {
                    throw new ReleaseFailureException(
                        "WindowsPeVersionFloorExceeded",
                        $"PE file '{Path.GetFileName(path)}' requires Windows {operatingSystemVersion}/subsystem " +
                        $"{subsystemVersion}, above the declared {runtime.MinimumOsVersion} boundary.");
                }

                evidenceLines.Add(
                    $"{Path.GetFileName(path)}: PE OS {operatingSystemVersion}, subsystem {subsystemVersion}");
            }

            evidenceLines.Add(
                "enforcement: exact worker-kernel floor, per-file PE architecture/version headers, " +
                "complete import allowlist, explicit compiler CPU target, runtime CPU dispatch, smoke test, and hardening gates");
        }
        else if (runtime.Os == "linux")
        {
            string maximumAllowed = runtime.MinimumLibcVersion!;
            bool foundVersion = false;
            foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal))
            {
                CommandResult result = await processRunner.RunAsync(
                    "readelf",
                    ["-W", "--version-info", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "AbiCompatibilityInspectionFailed");
                string[] versions = ParseGlibcVersions(result.StandardOutput + result.StandardError);
                if (versions.Any(version => CompareDottedVersions(version, maximumAllowed) > 0))
                {
                    string incompatible = versions.Last(version => CompareDottedVersions(version, maximumAllowed) > 0);
                    throw new ReleaseFailureException(
                        "GlibcVersionFloorExceeded",
                        $"Native file '{Path.GetFileName(path)}' requires GLIBC_{incompatible}, above the declared GLIBC_{maximumAllowed} boundary.");
                }

                if (versions.Length > 0)
                {
                    foundVersion = true;
                    evidenceLines.Add($"{Path.GetFileName(path)}: maximum GLIBC_{versions[^1]}");
                }
                else
                {
                    evidenceLines.Add($"{Path.GetFileName(path)}: no GLIBC symbol versions");
                }
            }

            if (!foundVersion)
            {
                throw new ReleaseFailureException(
                    "AbiCompatibilityInspectionInvalid",
                    "The glibc payload did not expose any versioned GLIBC symbol requirements.");
            }

            await ValidateElfInterpretersAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
            evidenceLines.Add("interpreter: " + ExpectedElfInterpreter(runtime));
        }
        else if (runtime.Os == "linux-musl")
        {
            await ValidateElfInterpretersAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
            evidenceLines.Add("interpreter: " + ExpectedElfInterpreter(runtime));
            evidenceLines.Add("workerImage: " + runtime.WorkerImage);
            evidenceLines.Add("enforcement: pinned Alpine worker plus exact musl interpreter and dependency allowlist");
        }
        else if (runtime.Os == "macos")
        {
            string expectedMinimum = runtime.MinimumOsVersion!;
            foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal))
            {
                CommandResult result = await processRunner.RunAsync(
                    "otool",
                    ["-l", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "AbiCompatibilityInspectionFailed");
                string[] minimumVersions = ParseMacMinimumOsVersions(result.StandardOutput);
                if (minimumVersions.Length != 1 ||
                    CompareDottedVersions(minimumVersions[0], expectedMinimum) != 0)
                {
                    throw new ReleaseFailureException(
                        "MacDeploymentTargetMismatch",
                        $"Native file '{Path.GetFileName(path)}' does not record the exact macOS {expectedMinimum} deployment target.");
                }

                if (!result.StandardOutput.Contains("path @loader_path ", StringComparison.Ordinal))
                {
                    throw new ReleaseFailureException(
                        "RuntimeSearchPathInvalid",
                        $"Native file '{Path.GetFileName(path)}' does not have the required application-local @loader_path runpath.");
                }

                evidenceLines.Add($"{Path.GetFileName(path)}: macOS {minimumVersions[0]}, @loader_path");
            }
        }
        else
        {
            throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is unsupported.");
        }

        string evidence = string.Join('\n', evidenceLines);
        await File.WriteAllTextAsync(
            Path.Combine(payloadRoot, "BUILD-METADATA", "abi-compatibility.txt"),
            evidence + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    internal static (string OperatingSystemVersion, string SubsystemVersion) ParseWindowsPeVersions(string evidence)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in evidence.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 &&
                (fields[0] is "MajorOSystemVersion" or "MinorOSystemVersion" or
                    "MajorSubsystemVersion" or "MinorSubsystemVersion") &&
                int.TryParse(
                    fields[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int value))
            {
                values[fields[0]] = value;
            }
        }

        string[] required =
        [
            "MajorOSystemVersion",
            "MinorOSystemVersion",
            "MajorSubsystemVersion",
            "MinorSubsystemVersion"
        ];
        if (required.Any(field => !values.ContainsKey(field)))
        {
            throw new ReleaseFailureException(
                "WindowsPeVersionInspectionInvalid",
                "The PE header did not report complete operating-system and subsystem versions.");
        }

        return (
            $"{values["MajorOSystemVersion"]}.{values["MinorOSystemVersion"]}",
            $"{values["MajorSubsystemVersion"]}.{values["MinorSubsystemVersion"]}");
    }

    private async Task ValidateElfInterpretersAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string expected = ExpectedElfInterpreter(runtime);
        foreach (string fileName in new[] { "ffmpeg", "ffprobe" })
        {
            string path = Path.Combine(payloadRoot, fileName);
            CommandResult result = await processRunner.RunAsync(
                "readelf",
                ["-W", "-l", path],
                payloadRoot,
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "AbiCompatibilityInspectionFailed");
            if (!result.StandardOutput.Contains($"Requesting program interpreter: {expected}", StringComparison.Ordinal))
            {
                throw new ReleaseFailureException(
                    "ElfInterpreterMismatch",
                    $"Native executable '{fileName}' does not use the declared '{expected}' interpreter.");
            }
        }
    }

    private static string ExpectedElfInterpreter(RuntimeDefinition runtime) => (runtime.Os, runtime.Architecture) switch
    {
        ("linux", "x64") => "/lib64/ld-linux-x86-64.so.2",
        ("linux", "arm64") => "/lib/ld-linux-aarch64.so.1",
        ("linux-musl", "x64") => "/lib/ld-musl-x86_64.so.1",
        ("linux-musl", "arm64") => "/lib/ld-musl-aarch64.so.1",
        _ => throw new ReleaseFailureException(
            "UnsupportedElfInterpreter",
            $"Runtime Identifier '{runtime.Rid}' has no approved ELF interpreter.")
    };

    internal static string[] ParseGlibcVersions(string evidence) =>
        GlibcVersionPattern.Matches(evidence)
            .Select(match => match.Groups["version"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(Comparer<string>.Create(CompareDottedVersions))
            .ToArray();

    internal static string[] ParseMacMinimumOsVersions(string evidence)
    {
        string? command = null;
        var versions = new List<string>();
        foreach (string line in evidence.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("cmd ", StringComparison.Ordinal))
            {
                command = line["cmd ".Length..];
            }
            else if (command == "LC_BUILD_VERSION" && line.StartsWith("minos ", StringComparison.Ordinal))
            {
                versions.Add(line["minos ".Length..]);
            }
            else if (command == "LC_VERSION_MIN_MACOSX" && line.StartsWith("version ", StringComparison.Ordinal))
            {
                versions.Add(line["version ".Length..]);
            }
        }

        return versions
            .Distinct(StringComparer.Ordinal)
            .Order(Comparer<string>.Create(CompareDottedVersions))
            .ToArray();
    }

    internal static int CompareDottedVersions(string left, string right)
    {
        int[] leftParts = ParseDottedVersion(left);
        int[] rightParts = ParseDottedVersion(right);
        for (int index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            int leftPart = index < leftParts.Length ? leftParts[index] : 0;
            int rightPart = index < rightParts.Length ? rightParts[index] : 0;
            int comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int[] ParseDottedVersion(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 3 ||
            parts.Any(part => !int.TryParse(
                part,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out _)))
        {
            throw new ReleaseFailureException("InvalidCompatibilityVersion", $"Compatibility version '{value}' is invalid.");
        }

        return parts.Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private async Task<string> InspectHardeningAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        var evidenceLines = new List<string>();
        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(path);
            if (runtime.Os == "windows")
            {
                CommandResult result = await processRunner.RunAsync(
                    "objdump",
                    ["-p", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "HardeningInspectionFailed");
                if (!result.StandardOutput.Contains("DYNAMIC_BASE", StringComparison.Ordinal) ||
                    !result.StandardOutput.Contains("NX_COMPAT", StringComparison.Ordinal))
                {
                    throw new ReleaseFailureException(
                        "NativeHardeningMissing",
                        $"PE file '{fileName}' does not enable ASLR and NX compatibility.");
                }

                evidenceLines.Add($"{fileName}: ASLR, NX_COMPAT");
            }
            else if (runtime.Os is "linux" or "linux-musl")
            {
                CommandResult result = await processRunner.RunAsync(
                    "readelf",
                    ["-W", "-h", "-l", "-d", "-s", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "HardeningInspectionFailed");
                string output = result.StandardOutput;
                string? stackLine = output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SingleOrDefault(line => line.StartsWith("GNU_STACK", StringComparison.Ordinal));
                string[] stackColumns = stackLine?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                bool nonExecutableStack = stackColumns.Length >= 2 &&
                    !stackColumns[^2].Contains('E', StringComparison.Ordinal);
                bool positionIndependent = output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(line => line.StartsWith("Type:", StringComparison.Ordinal) &&
                                 line.Contains("DYN", StringComparison.Ordinal));
                bool relro = output.Contains("GNU_RELRO", StringComparison.Ordinal);
                bool immediateBinding = output.Contains("(BIND_NOW)", StringComparison.Ordinal) ||
                    output.Contains("Flags: NOW", StringComparison.Ordinal);
                bool stackProtector = output.Contains("__stack_chk_fail", StringComparison.Ordinal);
                bool executablePie = fileName is not ("ffmpeg" or "ffprobe") ||
                    output
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(line => line.Contains("Flags:", StringComparison.Ordinal) &&
                                     line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                         .Contains("PIE", StringComparer.Ordinal));
                if (!nonExecutableStack || !positionIndependent || !relro || !immediateBinding ||
                    !stackProtector || !executablePie)
                {
                    throw new ReleaseFailureException(
                        "NativeHardeningMissing",
                        $"ELF file '{fileName}' does not satisfy the NX, PIE, RELRO, immediate-binding, and stack-protector baseline.");
                }

                evidenceLines.Add($"{fileName}: NX stack, PIE/PIC, RELRO, BIND_NOW, stack protector");
            }
            else if (runtime.Os == "macos")
            {
                CommandResult header = await processRunner.RunAsync(
                    "otool",
                    ["-hv", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(header, "HardeningInspectionFailed");
                bool executablePie = fileName is not ("ffmpeg" or "ffprobe") ||
                    header.StandardOutput
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                            .Contains("PIE", StringComparer.Ordinal));
                CommandResult symbols = await processRunner.RunAsync(
                    "nm",
                    ["-u", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(symbols, "HardeningInspectionFailed");
                if (!executablePie ||
                    !symbols.StandardOutput.Contains("___stack_chk_fail", StringComparison.Ordinal))
                {
                    throw new ReleaseFailureException(
                        "NativeHardeningMissing",
                        $"Mach-O file '{fileName}' does not satisfy the PIE and stack-protector baseline.");
                }

                evidenceLines.Add($"{fileName}: PIE/PIC, stack protector");
            }
            else
            {
                throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is unsupported.");
            }
        }

        string evidence = string.Join('\n', evidenceLines);
        await File.WriteAllTextAsync(
            Path.Combine(payloadRoot, "BUILD-METADATA", "hardening.txt"),
            evidence + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    internal static string CanonicalizeWindowsDependencyEvidence(string evidence)
    {
        string[] dependencies = ParseWindowsDependencies(evidence);
        if (dependencies.Length == 0)
        {
            throw new ReleaseFailureException(
                "DynamicDependencyInspectionInvalid",
                "The Windows dependency inspection reported no imported DLLs.");
        }

        return string.Join('\n', dependencies.Select(name => $"DLL Name: {name}"));
    }

    private async Task<IReadOnlyList<string>> InspectWindowsDependenciesAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            "objdump",
            ["-p", path],
            workingDirectory,
            TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "DynamicDependencyInspectionFailed");
        string[] dependencies = ParseWindowsDependencies(result.StandardOutput + result.StandardError);
        if (dependencies.Length == 0)
        {
            throw new ReleaseFailureException(
                "DynamicDependencyInspectionInvalid",
                $"The Windows dependency inspection reported no imported DLLs for '{Path.GetFileName(path)}'.");
        }

        return dependencies;
    }

    private static string[] ParseWindowsDependencies(string evidence) =>
        evidence
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("DLL Name:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["DLL Name:".Length..].Trim())
            .Where(name => name.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        else if (runtime.Os == "windows")
        {
            environment["PATH"] = string.Empty;
        }

        CommandResult generate = await processRunner.RunAsync(
            ffmpeg,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-cpuflags", "0",
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
            ["-v", "error", "-cpuflags", "0", "-show_streams", "-show_format", "-of", "json", output],
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

    private async Task ValidateBuildBaselineAsync(
        string workingDirectory,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Os == "windows")
        {
            Version actual = Environment.OSVersion.Version;
            string actualBoundary = $"{actual.Major}.{actual.Minor}.{actual.Build}";
            if (!actualBoundary.Equals(runtime.MinimumOsVersion, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException(
                    "WorkerOperatingSystemBaselineMismatch",
                    $"Runtime Identifier '{runtime.Rid}' must be built and smoke-tested on exact Windows kernel " +
                    $"{runtime.MinimumOsVersion}; this worker is {actualBoundary}.");
            }

            return;
        }

        if (runtime.Os == "linux")
        {
            string osRelease = await File.ReadAllTextAsync("/etc/os-release", cancellationToken).ConfigureAwait(false);
            if (!ReadOsReleaseValue(osRelease, "ID").Equals("ubuntu", StringComparison.Ordinal) ||
                !ReadOsReleaseValue(osRelease, "VERSION_ID").Equals(runtime.MinimumOsVersion, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException(
                    "WorkerOperatingSystemBaselineMismatch",
                    $"Runtime Identifier '{runtime.Rid}' must be built on Ubuntu {runtime.MinimumOsVersion}.");
            }

            CommandResult result = await processRunner.RunAsync(
                "getconf",
                ["GNU_LIBC_VERSION"],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "WorkerLibcBaselineInspectionFailed");
            string actualVersion = result.StandardOutput
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? string.Empty;
            if (CompareDottedVersions(actualVersion, runtime.MinimumLibcVersion!) != 0)
            {
                throw new ReleaseFailureException(
                    "WorkerLibcBaselineMismatch",
                    $"Runtime Identifier '{runtime.Rid}' must be built against glibc {runtime.MinimumLibcVersion}.");
            }

            return;
        }

        if (runtime.Os == "linux-musl")
        {
            string alpineVersion = (await File.ReadAllTextAsync("/etc/alpine-release", cancellationToken).ConfigureAwait(false)).Trim();
            if (!(alpineVersion.Equals(runtime.MinimumOsVersion, StringComparison.Ordinal) ||
                  alpineVersion.StartsWith(runtime.MinimumOsVersion + ".", StringComparison.Ordinal)) ||
                !string.Equals(
                    Environment.GetEnvironmentVariable("SUPPROCOM_WORKER_IMAGE"),
                    runtime.WorkerImage,
                    StringComparison.Ordinal))
            {
                throw new ReleaseFailureException(
                    "WorkerOperatingSystemBaselineMismatch",
                    $"Runtime Identifier '{runtime.Rid}' must use its pinned Alpine {runtime.MinimumOsVersion} worker image.");
            }

            string loader = ExpectedElfInterpreter(runtime);
            CommandResult result = await processRunner.RunAsync(
                loader,
                [],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Match version = MuslVersionPattern.Match(result.StandardOutput + result.StandardError);
            if (!version.Success ||
                CompareDottedVersions(version.Groups["version"].Value, runtime.MinimumLibcVersion!) != 0)
            {
                throw new ReleaseFailureException(
                    "WorkerLibcBaselineMismatch",
                    $"Runtime Identifier '{runtime.Rid}' must be built against musl {runtime.MinimumLibcVersion}.");
            }

            return;
        }

        if (runtime.Os == "macos")
        {
            CommandResult result = await processRunner.RunAsync(
                "sw_vers",
                ["-productVersion"],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "WorkerOperatingSystemBaselineInspectionFailed");
            string actualVersion = result.StandardOutput.Trim();
            int[] parts = ParseDottedVersion(actualVersion);
            int[] baseline = ParseDottedVersion(runtime.MinimumOsVersion!);
            if (parts[0] != baseline[0] || CompareDottedVersions(actualVersion, runtime.MinimumOsVersion!) < 0)
            {
                throw new ReleaseFailureException(
                    "WorkerOperatingSystemBaselineMismatch",
                    $"Runtime Identifier '{runtime.Rid}' must be built on macOS {runtime.MinimumOsVersion}.x.");
            }

            return;
        }

        throw new ReleaseFailureException("UnsupportedWorkerOS", $"Worker OS '{runtime.Os}' is unsupported.");
    }

    internal static string ReadOsReleaseValue(string content, string key)
    {
        string prefix = key + "=";
        string? line = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? string.Empty : line[prefix.Length..].Trim('"', '\'');
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
        string DynamicDependencyEvidence,
        string AbiCompatibilityEvidence,
        string HardeningEvidence);
}
