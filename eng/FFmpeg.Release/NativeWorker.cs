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
    private static readonly Regex ApkPackageIdentityPattern = new(
        @"^(?<name>.+)-(?<version>\d.+)$",
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
            flavor,
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
            4,
            planHash,
            plan.Version,
            plan.PackageVersion,
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
        ApplySourceCompatibilityFixups(sourceRoot, runtime);

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
        ValidateGeneratedConfiguration(sourceRoot, flavor);

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
        IReadOnlyList<BundledComponent> bundledComponents = await BundleRuntimeDependenciesAsync(
            installedBin,
            runtime,
            cancellationToken).ConfigureAwait(false);
        await NormalizeRuntimeSearchPathAsync(installedBin, runtime, cancellationToken).ConfigureAwait(false);
        await StripBinariesAsync(
            installedBin,
            runtime,
            version.SourceDateEpoch.Value,
            cancellationToken).ConfigureAwait(false);
        await SignMacPayloadAsync(installedBin, runtime, cancellationToken).ConfigureAwait(false);
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
            bundledComponents,
            configureArguments);
        ValidateExpectedPayload(payloadRoot, flavor, runtime);
        string architectureEvidence = await InspectArchitectureAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string dependencyEvidence = await InspectDynamicDependenciesAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string abiEvidence = await InspectAbiCompatibilityAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        string hardeningEvidence = await InspectHardeningAsync(payloadRoot, runtime, cancellationToken).ConfigureAwait(false);
        NormalizePayloadPermissions(payloadRoot);
        RejectAbsoluteBuildPath(payloadRoot, workRoot);
        await ValidateFeatureContractAsync(payloadRoot, flavor, runtime, cancellationToken).ConfigureAwait(false);
        await RunSmokeTestAsync(payloadRoot, flavor, runtime, cancellationToken).ConfigureAwait(false);
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
                string homebrewPrefix = HomebrewPrefix(runtime);
                arguments.Add("--target-os=darwin");
                arguments.Add($"--arch={ToConfigureArchitecture(runtime.Architecture)}");
                arguments.Add("--enable-pthreads");
                arguments.Add("--cc=clang");
                arguments.Add("--enable-audiotoolbox");
                arguments.Add("--enable-coreimage");
                arguments.Add("--enable-metal");
                arguments.Add("--enable-videotoolbox");
                arguments.Add("--install-name-dir=@rpath");
                arguments.Add(
                    $"--extra-cflags=-I{homebrewPrefix}/include {compilerFlags} -mmacosx-version-min={runtime.MinimumOsVersion} -fstack-protector-strong");
                arguments.Add(
                    $"--extra-ldflags=-L{homebrewPrefix}/lib -Wl,-reproducible -Wl,-rpath,@loader_path -mmacosx-version-min={runtime.MinimumOsVersion}");
                arguments.Add("--extra-libs=-liconv");
                break;
            case "windows":
                arguments.Add("--target-os=mingw32");
                arguments.Add($"--arch={ToConfigureArchitecture(runtime.Architecture)}");
                arguments.Add("--enable-w32threads");
                arguments.Add("--enable-d3d11va");
                arguments.Add("--enable-d3d12va");
                arguments.Add("--enable-dxva2");
                arguments.Add("--enable-mediafoundation");
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
                arguments.Add("--extra-libs=-liconv");
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

    private static void ApplySourceCompatibilityFixups(string sourceRoot, RuntimeDefinition runtime)
    {
        if (runtime.Os != "windows" || runtime.Architecture != "arm64")
        {
            return;
        }

        string versionPath = Path.Combine(sourceRoot, "VERSION");
        string relocatedVersionPath = Path.Combine(sourceRoot, "FFMPEG_VERSION");
        string versionScriptPath = Path.Combine(sourceRoot, "ffbuild", "version.sh");
        if (!File.Exists(versionPath) || File.Exists(relocatedVersionPath) || !File.Exists(versionScriptPath))
        {
            throw new ReleaseFailureException(
                "SourceCompatibilityFixupFailed",
                "The FFmpeg release-version inputs required by the Windows ARM64 source fixup are missing.");
        }

        const string originalCommand = "cat VERSION";
        const string replacementCommand = "cat FFMPEG_VERSION";
        string versionScript = File.ReadAllText(versionScriptPath);
        if (versionScript.Split(originalCommand, StringSplitOptions.None).Length != 2)
        {
            throw new ReleaseFailureException(
                "SourceCompatibilityFixupFailed",
                "The FFmpeg version script no longer has the expected single release-version reference.");
        }

        File.Move(versionPath, relocatedVersionPath);
        File.WriteAllText(
            versionScriptPath,
            versionScript.Replace(originalCommand, replacementCommand, StringComparison.Ordinal),
            new UTF8Encoding(false));
    }

    private static void ValidateGeneratedConfiguration(string sourceRoot, FlavorDefinition flavor)
    {
        HashSet<string> config = File.ReadLines(Path.Combine(sourceRoot, "ffbuild", "config.mak"))
            .ToHashSet(StringComparer.Ordinal);
        if (config.Contains("CONFIG_NONFREE=yes") ||
            !config.Contains("CONFIG_SHARED=yes") ||
            config.Contains("CONFIG_STATIC=yes"))
        {
            throw new ReleaseFailureException(
                "GeneratedLicenseConfigurationInvalid",
                "The generated FFmpeg configuration crossed the approved redistributable shared-library boundary.");
        }

        bool gplEnabled = config.Contains("CONFIG_GPL=yes");
        bool expectedGpl = flavor.Name.Equals("full", StringComparison.Ordinal);
        if (gplEnabled != expectedGpl ||
            (expectedGpl && !config.Contains("CONFIG_VERSION3=yes")))
        {
            throw new ReleaseFailureException(
                "GeneratedLicenseConfigurationInvalid",
                $"The generated FFmpeg configuration does not match the declared '{flavor.Name}' license family.");
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
                ? ["-x", "-no_uuid", path]
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

    private async Task SignMacPayloadAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Os != "macos")
        {
            return;
        }

        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal))
        {
            CommandResult sign = await processRunner.RunAsync(
                "codesign",
                ["--force", "--sign", "-", "--timestamp=none", path],
                payloadRoot,
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(sign, "MacAdHocSigningFailed");

            CommandResult verify = await processRunner.RunAsync(
                "codesign",
                ["--verify", "--strict", path],
                payloadRoot,
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(verify, "MacAdHocSigningFailed");
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
                name.Equals("ffplay" + executableSuffix, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".so", StringComparison.Ordinal) ||
                name.EndsWith(".dylib", StringComparison.Ordinal) ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private async Task<IReadOnlyList<BundledComponent>> BundleRuntimeDependenciesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        return runtime.Os switch
        {
            "windows" => await BundleWindowsDependenciesAsync(
                payloadRoot,
                runtime,
                cancellationToken).ConfigureAwait(false),
            "linux" or "linux-musl" => await BundleElfDependenciesAsync(
                payloadRoot,
                runtime,
                cancellationToken).ConfigureAwait(false),
            "macos" => await BundleMacDependenciesAsync(
                payloadRoot,
                runtime,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ReleaseFailureException(
                "UnsupportedWorkerOS",
                $"Worker OS '{runtime.Os}' is unsupported.")
        };
    }

    private async Task<IReadOnlyList<BundledComponent>> BundleWindowsDependenciesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        var components = new List<BundledComponent>();
        HashSet<string> systemDependencies = (runtime.SystemDependencies ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                if (systemDependencies.Contains(dependency))
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
                components.Add(await CopyWindowsDependencyLicensesAsync(
                    source,
                    dependency,
                    payloadRoot,
                    cancellationToken).ConfigureAwait(false));
                pending.Enqueue(destination);
            }
        }

        return components.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<BundledComponent>> BundleElfDependenciesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        var components = new List<BundledComponent>();
        HashSet<string> systemDependencies = (runtime.SystemDependencies ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal));
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out string? path))
        {
            if (!inspected.Add(path))
            {
                continue;
            }

            CommandResult readElf = await processRunner.RunAsync(
                "readelf",
                ["-W", "-d", path],
                payloadRoot,
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(readElf, "DynamicDependencyInspectionFailed");
            string[] dependencies = ParseElfDependencies(readElf.StandardOutput);
            IReadOnlyDictionary<string, string> resolved = await ResolveElfDependenciesAsync(
                path,
                payloadRoot,
                cancellationToken).ConfigureAwait(false);
            foreach (string dependency in dependencies)
            {
                string destination = Path.Combine(payloadRoot, dependency);
                if (File.Exists(destination) || systemDependencies.Contains(dependency))
                {
                    continue;
                }

                if (!resolved.TryGetValue(dependency, out string? source) || !File.Exists(source))
                {
                    throw new ReleaseFailureException(
                        "BundledDependencyMissing",
                        $"ELF dependency '{dependency}' required by '{Path.GetFileName(path)}' could not be resolved.");
                }

                File.Copy(source, destination, overwrite: false);
                components.Add(await CaptureElfDependencyComponentAsync(
                    source,
                    dependency,
                    payloadRoot,
                    runtime,
                    cancellationToken).ConfigureAwait(false));
                pending.Enqueue(destination);
            }
        }

        return components.OrderBy(item => item.FileName, StringComparer.Ordinal).ToArray();
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveElfDependenciesAsync(
        string path,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>
        {
            ["LD_LIBRARY_PATH"] = payloadRoot
        };
        CommandResult result = await processRunner.RunAsync(
            "ldd",
            [path],
            payloadRoot,
            TimeSpan.FromSeconds(60),
            environment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "DynamicDependencyResolutionFailed");
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (result.StandardOutput + result.StandardError).Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            string name = line[..arrow].Trim();
            string value = line[(arrow + 2)..].Trim();
            int metadata = value.IndexOf(" (", StringComparison.Ordinal);
            string resolved = metadata < 0 ? value : value[..metadata];
            if (name.Length != 0 && Path.IsPathFullyQualified(resolved))
            {
                dependencies[name] = resolved;
            }
        }

        return dependencies;
    }

    private async Task<IReadOnlyList<BundledComponent>> BundleMacDependenciesAsync(
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        var components = new List<BundledComponent>();
        HashSet<string> systemDependencies = (runtime.SystemDependencies ?? [])
            .ToHashSet(StringComparer.Ordinal);
        const string sdl3RuntimeName = "libSDL3.dylib";
        if (runtime.Os == "macos" && File.Exists(Path.Combine(payloadRoot, "ffplay")))
        {
            string sdl3Source = Path.Combine(HomebrewPrefix(runtime), "lib", sdl3RuntimeName);
            if (!File.Exists(sdl3Source))
            {
                throw new ReleaseFailureException(
                    "BundledDependencyMissing",
                    $"The Homebrew SDL2 compatibility runtime requires '{sdl3Source}', but it is not installed.");
            }

            string sdl3Destination = Path.Combine(payloadRoot, sdl3RuntimeName);
            File.Copy(sdl3Source, sdl3Destination, overwrite: false);
            MakeMacDependencyWritable(sdl3Destination);
            components.Add(await CaptureHomebrewDependencyComponentAsync(
                sdl3Source,
                sdl3RuntimeName,
                payloadRoot,
                cancellationToken).ConfigureAwait(false));
        }

        var pending = new Queue<string>(EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal));
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out string? path))
        {
            if (!inspected.Add(path))
            {
                continue;
            }

            foreach (string dependency in await ReadMacDependenciesAsync(
                         path,
                         payloadRoot,
                         cancellationToken).ConfigureAwait(false))
            {
                string name = Path.GetFileName(dependency);
                string destination = Path.Combine(payloadRoot, name);
                if (File.Exists(destination) || systemDependencies.Contains(dependency))
                {
                    continue;
                }

                string? source = ResolveMacDependencySource(dependency, runtime);
                if (source is null)
                {
                    throw new ReleaseFailureException(
                        "BundledDependencyMissing",
                        $"Mach-O dependency '{dependency}' required by '{Path.GetFileName(path)}' could not be resolved.");
                }

                File.Copy(source, destination, overwrite: false);
                MakeMacDependencyWritable(destination);

                components.Add(await CaptureHomebrewDependencyComponentAsync(
                    source,
                    name,
                    payloadRoot,
                    cancellationToken).ConfigureAwait(false));
                pending.Enqueue(destination);
            }
        }

        foreach (string path in EnumerateNativeFiles(payloadRoot, runtime).Order(StringComparer.Ordinal))
        {
            string[] dependencies = await ReadMacDependenciesAsync(
                path,
                payloadRoot,
                cancellationToken).ConfigureAwait(false);
            foreach (string dependency in dependencies)
            {
                if (systemDependencies.Contains(dependency))
                {
                    continue;
                }

                string name = Path.GetFileName(dependency);
                string local = Path.Combine(payloadRoot, name);
                if (!File.Exists(local))
                {
                    throw new ReleaseFailureException(
                        "BundledDependencyMissing",
                        $"Mach-O dependency '{dependency}' was not copied into the payload.");
                }

                string replacement = "@rpath/" + name;
                if (!dependency.Equals(replacement, StringComparison.Ordinal))
                {
                    CommandResult change = await processRunner.RunAsync(
                        "install_name_tool",
                        ["-change", dependency, replacement, path],
                        payloadRoot,
                        TimeSpan.FromSeconds(60),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(change, "MacDependencyRewriteFailed");
                }
            }

            if (path.EndsWith(".dylib", StringComparison.Ordinal))
            {
                CommandResult identity = await processRunner.RunAsync(
                    "install_name_tool",
                    ["-id", "@rpath/" + Path.GetFileName(path), path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(identity, "MacDependencyRewriteFailed");
            }

            CommandResult loadCommands = await processRunner.RunAsync(
                "otool",
                ["-l", path],
                payloadRoot,
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(loadCommands, "MacDependencyRewriteFailed");
            if (!loadCommands.StandardOutput.Contains("path @loader_path ", StringComparison.Ordinal))
            {
                CommandResult rpath = await processRunner.RunAsync(
                    "install_name_tool",
                    ["-add_rpath", "@loader_path", path],
                    payloadRoot,
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(rpath, "MacDependencyRewriteFailed");
            }
        }

        return components.OrderBy(item => item.FileName, StringComparer.Ordinal).ToArray();
    }

    private static string HomebrewPrefix(RuntimeDefinition runtime) =>
        runtime.Architecture == "arm64" ? "/opt/homebrew" : "/usr/local";

    private static void MakeMacDependencyWritable(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserWrite);
        }
    }

    private static string? ResolveMacDependencySource(string dependency, RuntimeDefinition runtime)
    {
        if (Path.IsPathFullyQualified(dependency))
        {
            return File.Exists(dependency) ? dependency : null;
        }

        if (!dependency.StartsWith("@rpath/", StringComparison.Ordinal) &&
            !dependency.StartsWith("@loader_path/", StringComparison.Ordinal) &&
            !dependency.StartsWith("@executable_path/", StringComparison.Ordinal))
        {
            return null;
        }

        string candidate = Path.Combine(HomebrewPrefix(runtime), "lib", Path.GetFileName(dependency));
        return File.Exists(candidate) ? candidate : null;
    }

    private async Task<string[]> ReadMacDependenciesAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            "otool",
            ["-L", path],
            workingDirectory,
            TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "DynamicDependencyInspectionFailed");
        return ParseMacDependencies(result.StandardOutput);
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

    private async Task<BundledComponent> CopyWindowsDependencyLicensesAsync(
        string runtimePath,
        string fileName,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        CommandResult unixPath = await processRunner.RunAsync(
            "cygpath",
            ["-u", runtimePath],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(unixPath, "WindowsRuntimeLicenseMissing");
        CommandResult owner = await processRunner.RunAsync(
            "pacman",
            ["-Qo", unixPath.StandardOutput.Trim()],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(owner, "WindowsRuntimeLicenseMissing");
        const string ownerMarker = " is owned by ";
        string ownerLine = owner.StandardOutput.Trim();
        int marker = ownerLine.IndexOf(ownerMarker, StringComparison.Ordinal);
        string[] ownerFields = marker < 0
            ? []
            : ownerLine[(marker + ownerMarker.Length)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string package = ownerFields.FirstOrDefault() ?? string.Empty;
        string version = ownerFields.Skip(1).FirstOrDefault() ?? string.Empty;
        if (package.Length == 0 || version.Length == 0)
        {
            throw new ReleaseFailureException(
                "WindowsRuntimeLicenseMissing",
                $"The package owner of dependency '{fileName}' could not be identified.");
        }

        string? packagePrefix = Environment.GetEnvironmentVariable("MINGW_PACKAGE_PREFIX");
        string licenseName = !string.IsNullOrWhiteSpace(packagePrefix) &&
            package.StartsWith(packagePrefix + "-", StringComparison.Ordinal)
            ? package[(packagePrefix.Length + 1)..]
            : package;
        string binDirectory = Path.GetDirectoryName(runtimePath)!;
        string prefix = Directory.GetParent(binDirectory)?.FullName ?? string.Empty;
        string source = Path.Combine(prefix, "share", "licenses", licenseName);
        string relativeLicensePath = Path.Combine(
            "licenses",
            "third-party",
            SanitizePackageDirectory(package));
        string destination = Path.Combine(payloadRoot, relativeLicensePath);
        if (!Directory.Exists(destination))
        {
            if (Directory.Exists(source))
            {
                CopyTree(source, destination);
            }
            else
            {
                string documentationSource = Path.Combine(prefix, "share", "doc", licenseName);
                if (Directory.Exists(documentationSource))
                {
                    CopyTree(documentationSource, destination);
                }
                else
                {
                    Directory.CreateDirectory(destination);
                }

                await File.WriteAllTextAsync(
                    Path.Combine(destination, "PACKAGE-SOURCE.txt"),
                    $"Package: {package}\n" +
                    $"Version: {version}\n" +
                    $"Official record and source-only archive: https://packages.msys2.org/packages/{Uri.EscapeDataString(package)}\n" +
                    "The installed binary archive has no dedicated share/licenses directory. " +
                    "Its available installed documentation and complete pacman license metadata are retained here.\n",
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }

            CommandResult metadata = await processRunner.RunAsync(
                "pacman",
                ["-Qi", package],
                payloadRoot,
                TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(metadata, "WindowsRuntimeLicenseMissing");
            await File.WriteAllTextAsync(
                Path.Combine(destination, "PACMAN-PACKAGE-METADATA.txt"),
                metadata.StandardOutput + metadata.StandardError,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
        }

        return new BundledComponent(
            fileName,
            package,
            version,
            "pacman",
            relativeLicensePath.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private async Task<BundledComponent> CaptureElfDependencyComponentAsync(
        string runtimePath,
        string fileName,
        string payloadRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        return runtime.Toolchain.PackageManager switch
        {
            "apt" => await CaptureAptDependencyComponentAsync(
                runtimePath,
                fileName,
                payloadRoot,
                cancellationToken).ConfigureAwait(false),
            "apk" => await CaptureApkDependencyComponentAsync(
                runtimePath,
                fileName,
                payloadRoot,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"No ownership inspector exists for '{runtime.Toolchain.PackageManager}'.")
        };
    }

    private async Task<BundledComponent> CaptureAptDependencyComponentAsync(
        string runtimePath,
        string fileName,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        CommandResult canonicalPath = await processRunner.RunAsync(
            "realpath",
            [runtimePath],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(canonicalPath, "RuntimeDependencyOwnerMissing");
        string ownedPath = canonicalPath.StandardOutput.Trim();
        CommandResult owner = await processRunner.RunAsync(
            "dpkg-query",
            ["-S", ownedPath],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(owner, "RuntimeDependencyOwnerMissing");
        string? ownerLine = owner.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.EndsWith(": " + ownedPath, StringComparison.Ordinal));
        int delimiter = ownerLine?.LastIndexOf(": ", StringComparison.Ordinal) ?? -1;
        string package = delimiter < 1 ? string.Empty : ownerLine![..delimiter];
        if (package.Length == 0)
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"The Debian package owner of '{runtimePath}' could not be parsed.");
        }

        CommandResult identity = await processRunner.RunAsync(
            "dpkg-query",
            ["-W", "-f=${binary:Package}\\t${Version}\\n", package],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(identity, "RuntimeDependencyOwnerMissing");
        string[] fields = identity.StandardOutput.Trim().Split('\t');
        if (fields.Length != 2 || fields.Any(string.IsNullOrWhiteSpace))
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"The Debian package identity for '{runtimePath}' is incomplete.");
        }

        string documentationPackage = fields[0].Split(':')[0];
        string licenseSource = Path.Combine("/usr/share/doc", documentationPackage, "copyright");
        if (!File.Exists(licenseSource))
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyLicenseMissing",
                $"Debian package '{fields[0]}' has no installed copyright file.");
        }

        string relativeLicensePath = Path.Combine(
            "licenses",
            "third-party",
            SanitizePackageDirectory(fields[0]));
        string destination = Path.Combine(payloadRoot, relativeLicensePath);
        Directory.CreateDirectory(destination);
        string copyrightDestination = Path.Combine(destination, "copyright");
        if (!File.Exists(copyrightDestination))
        {
            File.Copy(licenseSource, copyrightDestination, overwrite: false);
        }

        return new BundledComponent(
            fileName,
            fields[0],
            fields[1],
            "apt",
            relativeLicensePath.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private async Task<BundledComponent> CaptureApkDependencyComponentAsync(
        string runtimePath,
        string fileName,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        CommandResult canonicalPath = await processRunner.RunAsync(
            "realpath",
            [runtimePath],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(canonicalPath, "RuntimeDependencyOwnerMissing");
        CommandResult owner = await processRunner.RunAsync(
            "apk",
            ["info", "-W", canonicalPath.StandardOutput.Trim()],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(owner, "RuntimeDependencyOwnerMissing");
        string? versionedPackage = ParseApkOwnerIdentity(owner.StandardOutput);
        if (versionedPackage is null)
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"The Alpine package owner of '{runtimePath}' could not be parsed.");
        }

        Match packageIdentity = ApkPackageIdentityPattern.Match(versionedPackage);
        if (!packageIdentity.Success)
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"The Alpine package owner of '{runtimePath}' could not be parsed.");
        }

        string package = packageIdentity.Groups["name"].Value;
        string version = packageIdentity.Groups["version"].Value;
        string relativeLicensePath = Path.Combine(
            "licenses",
            "third-party",
            SanitizePackageDirectory(package));
        string destination = Path.Combine(payloadRoot, relativeLicensePath);
        if (!Directory.Exists(destination))
        {
            string licenseSource = Path.Combine("/usr/share/licenses", package);
            if (Directory.Exists(licenseSource))
            {
                CopyTree(licenseSource, destination);
            }
            else
            {
                Directory.CreateDirectory(destination);
                CommandResult metadata = await processRunner.RunAsync(
                    "apk",
                    ["info", "--all", package],
                    payloadRoot,
                    TimeSpan.FromSeconds(30),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                EnsureSuccess(metadata, "RuntimeDependencyLicenseMissing");
                await File.WriteAllTextAsync(
                    Path.Combine(destination, "APK-PACKAGE-METADATA.txt"),
                    metadata.StandardOutput + metadata.StandardError,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new BundledComponent(
            fileName,
            package,
            version,
            "apk",
            relativeLicensePath.Replace(Path.DirectorySeparatorChar, '/'));
    }

    internal static string? ParseApkOwnerIdentity(string evidence)
    {
        string[] lines = evidence.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            return null;
        }

        const string ownerMarker = " is owned by ";
        int markerIndex = lines[0].LastIndexOf(ownerMarker, StringComparison.Ordinal);
        string identity = markerIndex < 0
            ? lines[0]
            : lines[0][(markerIndex + ownerMarker.Length)..];
        return ApkPackageIdentityPattern.IsMatch(identity) ? identity : null;
    }

    private async Task<BundledComponent> CaptureHomebrewDependencyComponentAsync(
        string runtimePath,
        string fileName,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        CommandResult realPath = await processRunner.RunAsync(
            "realpath",
            [runtimePath],
            payloadRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(realPath, "RuntimeDependencyOwnerMissing");
        string resolved = realPath.StandardOutput.Trim();
        string marker = Path.DirectorySeparatorChar + "Cellar" + Path.DirectorySeparatorChar;
        int markerIndex = resolved.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"Homebrew dependency '{runtimePath}' is not inside the Cellar.");
        }

        string[] parts = resolved[(markerIndex + marker.Length)..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new ReleaseFailureException(
                "RuntimeDependencyOwnerMissing",
                $"The Homebrew formula identity for '{runtimePath}' is incomplete.");
        }

        string formula = parts[0];
        string version = parts[1];
        string cellarRoot = resolved[..(markerIndex + marker.Length)] + formula + Path.DirectorySeparatorChar + version;
        string relativeLicensePath = Path.Combine(
            "licenses",
            "third-party",
            SanitizePackageDirectory(formula));
        string destination = Path.Combine(payloadRoot, relativeLicensePath);
        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(destination);
            string[] licenseFiles = Directory.EnumerateFiles(cellarRoot, "*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("COPYRIGHT", StringComparison.OrdinalIgnoreCase);
                })
                .Where(path => new FileInfo(path).Length <= 2 * 1024 * 1024)
                .Order(StringComparer.Ordinal)
                .Take(64)
                .ToArray();
            foreach (string licenseFile in licenseFiles)
            {
                string relative = Path.GetRelativePath(cellarRoot, licenseFile);
                string licenseDestination = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(licenseDestination)!);
                File.Copy(licenseFile, licenseDestination, overwrite: false);
            }

            CommandResult metadata = await processRunner.RunAsync(
                "brew",
                ["info", "--json=v2", formula],
                payloadRoot,
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureSuccess(metadata, "RuntimeDependencyLicenseMissing");
            await File.WriteAllTextAsync(
                Path.Combine(destination, "HOMEBREW-FORMULA.json"),
                metadata.StandardOutput,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
        }

        return new BundledComponent(
            fileName,
            formula,
            version,
            "homebrew",
            relativeLicensePath.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static string SanitizePackageDirectory(string package) =>
        new(package.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_').ToArray());

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
        IReadOnlyList<BundledComponent> bundledComponents,
        IReadOnlyList<string> configureArguments)
    {
        File.WriteAllText(Path.Combine(payloadRoot, ".rid"), runtime.Rid + "\n", new UTF8Encoding(false));
        string licenses = Path.Combine(payloadRoot, "licenses");
        Directory.CreateDirectory(licenses);
        string[] licenseFiles = flavor.Name.Equals("full", StringComparison.Ordinal)
            ? ["COPYING.GPLv2", "COPYING.GPLv3", "COPYING.LGPLv2.1", "COPYING.LGPLv3", "LICENSE.md"]
            : ["COPYING.LGPLv2.1", "COPYING.LGPLv3", "LICENSE.md"];
        foreach (string name in licenseFiles)
        {
            File.Copy(Path.Combine(sourceRoot, name), Path.Combine(licenses, name), overwrite: false);
        }

        string notices = Path.Combine(payloadRoot, "notices");
        Directory.CreateDirectory(notices);
        File.WriteAllText(
            Path.Combine(notices, "THIRD-PARTY-NOTICES.txt"),
            $"This is the {flavor.Name} redistributable FFmpeg family ({flavor.LicenseExpression}).\n" +
            "The build includes the external codec, subtitle, image, transport, and filtering libraries named in " +
            "BUILD-METADATA/config.mak and BUILD-METADATA/configure-arguments.txt.\n" +
            "Every application-local dependency and its owning package/version are recorded in " +
            "BUILD-METADATA/bundled-components.json. Installed license or package-license evidence is under licenses/third-party.\n" +
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
        File.WriteAllBytes(
            Path.Combine(metadata, "bundled-components.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                bundledComponents.ToList(),
                ReleaseJsonContext.Default.ListBundledComponent));
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
                    sourceCompatibilityFixup = runtime.Os == "windows" && runtime.Architecture == "arm64"
                        ? "Renamed upstream VERSION to FFMPEG_VERSION and redirected ffbuild/version.sh to avoid a case-insensitive collision with the C++ <version> header."
                        : null,
                    buildRepository = "https://github.com/Supprocom/FFmpeg.Binaries"
                },
                IndentedJson) + "\n",
            new UTF8Encoding(false));
        File.Copy(
            Path.Combine(repositoryRoot, "eng", "release-matrix.json"),
            Path.Combine(metadata, "release-matrix.json"));
    }

    private static void ValidateExpectedPayload(
        string payloadRoot,
        FlavorDefinition flavor,
        RuntimeDefinition runtime)
    {
        string suffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        string[] requiredTools = flavor.IncludeFfplay
            ? ["ffmpeg" + suffix, "ffprobe" + suffix, "ffplay" + suffix]
            : ["ffmpeg" + suffix, "ffprobe" + suffix];
        foreach (string name in requiredTools)
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
            throw new ReleaseFailureException("SharedLibrariesMissing", "The worker payload contains no shared FFmpeg libraries.");
        }

        if (runtime.Os == "macos" && flavor.IncludeFfplay &&
            !File.Exists(Path.Combine(payloadRoot, "libSDL3.dylib")))
        {
            throw new ReleaseFailureException(
                "RequiredRuntimeMissing",
                "The macOS FFplay payload is missing the SDL3 runtime required by sdl2-compat.");
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

            if (dependencies.Length == 0 && IsFfmpegPayloadFile(Path.GetFileName(path), runtime))
            {
                throw new ReleaseFailureException(
                    "DynamicDependencyInspectionInvalid",
                    $"The dependency inspection reported no native dependencies for '{Path.GetFileName(path)}'.");
            }

            evidenceLines.Add(Path.GetFileName(path) + ":");
            if (dependencies.Length == 0)
            {
                evidenceLines.Add("  none");
            }

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
                    CompareDottedVersions(minimumVersions[0], expectedMinimum) > 0)
                {
                    throw new ReleaseFailureException(
                        "MacDeploymentTargetMismatch",
                        $"Native file '{Path.GetFileName(path)}' requires macOS {minimumVersions.FirstOrDefault() ?? "unknown"}, " +
                        $"above the declared macOS {expectedMinimum} boundary.");
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
        foreach (string fileName in new[] { "ffmpeg", "ffprobe", "ffplay" })
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
                bool ffmpegFile = IsFfmpegPayloadFile(fileName, runtime);
                bool stackProtector = !ffmpegFile || output.Contains("__stack_chk_fail", StringComparison.Ordinal);
                bool executablePie = fileName is not ("ffmpeg" or "ffprobe" or "ffplay") ||
                    output
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(line => line.Contains("Flags:", StringComparison.Ordinal) &&
                                     line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                         .Contains("PIE", StringComparer.Ordinal));
                if (!nonExecutableStack || !positionIndependent || !relro ||
                    (ffmpegFile && !immediateBinding) || !stackProtector || !executablePie)
                {
                    throw new ReleaseFailureException(
                        "NativeHardeningMissing",
                        $"ELF file '{fileName}' does not satisfy the structural hardening and FFmpeg stack-protector baseline.");
                }

                evidenceLines.Add(
                    $"{fileName}: NX stack, PIE/PIC, RELRO, " +
                    (immediateBinding ? "BIND_NOW" : "lazy binding") +
                    (ffmpegFile ? ", stack protector" : ", repository-built dependency"));
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
                bool executablePie = fileName is not ("ffmpeg" or "ffprobe" or "ffplay") ||
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
                bool ffmpegFile = IsFfmpegPayloadFile(fileName, runtime);
                if (!executablePie ||
                    (ffmpegFile && !symbols.StandardOutput.Contains("___stack_chk_fail", StringComparison.Ordinal)))
                {
                    throw new ReleaseFailureException(
                        "NativeHardeningMissing",
                        $"Mach-O file '{fileName}' does not satisfy the PIE/PIC and FFmpeg stack-protector baseline.");
                }

                evidenceLines.Add(
                    $"{fileName}: PIE/PIC" +
                    (ffmpegFile ? ", stack protector" : ", Homebrew dependency"));
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

    private static bool IsFfmpegPayloadFile(string fileName, RuntimeDefinition runtime)
    {
        string executableSuffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        if (fileName.Equals("ffmpeg" + executableSuffix, StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("ffprobe" + executableSuffix, StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("ffplay" + executableSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] libraryNames =
        [
            "avcodec",
            "avdevice",
            "avfilter",
            "avformat",
            "avutil",
            "postproc",
            "swresample",
            "swscale"
        ];
        return libraryNames.Any(name => fileName.Contains(name, StringComparison.OrdinalIgnoreCase));
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

    private async Task ValidateFeatureContractAsync(
        string payloadRoot,
        FlavorDefinition flavor,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string suffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        string ffmpeg = Path.Combine(payloadRoot, "ffmpeg" + suffix);
        Dictionary<string, string?> environment = CreatePayloadEnvironment(payloadRoot, runtime);
        (string Name, IReadOnlyList<string> Required)[] inventories =
        [
            ("encoders", flavor.RequiredEncoders),
            ("decoders", flavor.RequiredDecoders),
            ("filters", flavor.RequiredFilters),
            ("protocols", flavor.RequiredProtocols)
        ];
        string featureDirectory = Path.Combine(payloadRoot, "BUILD-METADATA", "features");
        Directory.CreateDirectory(featureDirectory);
        foreach ((string name, IReadOnlyList<string> required) in inventories)
        {
            CommandResult result = await processRunner.RunAsync(
                ffmpeg,
                ["-hide_banner", "-" + name],
                payloadRoot,
                SmokeTimeout,
                environment,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "FeatureInventoryFailed");
            string evidence = result.StandardOutput + result.StandardError;
            await File.WriteAllTextAsync(
                Path.Combine(featureDirectory, name + ".txt"),
                evidence,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            HashSet<string> available = ParseFeatureInventory(evidence);
            string[] missing = required.Where(item => !available.Contains(item)).ToArray();
            if (missing.Length != 0)
            {
                throw new ReleaseFailureException(
                    "FeatureContractIncomplete",
                    $"The {runtime.Rid} {flavor.Name} payload is missing required {name}: {string.Join(", ", missing)}.");
            }
        }

        if (flavor.Name.Equals("lgpl", StringComparison.Ordinal))
        {
            string encoderEvidence = await File.ReadAllTextAsync(
                Path.Combine(featureDirectory, "encoders.txt"),
                cancellationToken).ConfigureAwait(false);
            HashSet<string> encoders = ParseFeatureInventory(encoderEvidence);
            string[] forbidden = ["libx264", "libx265", "libxvid"];
            if (forbidden.Any(encoders.Contains))
            {
                throw new ReleaseFailureException(
                    "LgplFeatureBoundaryCrossed",
                    "The LGPL-only payload exposes a GPL encoder.");
            }
        }

        CommandResult ffplay = await processRunner.RunAsync(
            Path.Combine(payloadRoot, "ffplay" + suffix),
            ["-version"],
            payloadRoot,
            SmokeTimeout,
            environment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(ffplay, "FFplaySmokeFailed");
    }

    internal static HashSet<string> ParseFeatureInventory(string evidence)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in evidence.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 1)
            {
                features.Add(fields[0]);
            }
            else if (fields.Length >= 2 && fields[0].All(character =>
                         character == '.' || character == '-' || char.IsAsciiLetter(character)))
            {
                features.Add(fields[1]);
            }
        }

        return features;
    }

    private async Task RunSmokeTestAsync(
        string payloadRoot,
        FlavorDefinition flavor,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string suffix = runtime.Os == "windows" ? ".exe" : string.Empty;
        string ffmpeg = Path.Combine(payloadRoot, "ffmpeg" + suffix);
        string ffprobe = Path.Combine(payloadRoot, "ffprobe" + suffix);
        string output = Path.Combine(payloadRoot, "smoke-output.mkv");
        Dictionary<string, string?> environment = CreatePayloadEnvironment(payloadRoot, runtime);

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

        (string Encoder, string[] Options)[] codecSmokes = flavor.Name.Equals("full", StringComparison.Ordinal)
            ?
            [
                ("libx264", ["-preset", "ultrafast"]),
                ("libx265", ["-preset", "ultrafast"]),
                ("libvpx-vp9", ["-deadline", "realtime", "-cpu-used", "8"]),
                ("libaom-av1", ["-cpu-used", "8"])
            ]
            :
            [
                ("libopenh264", []),
                ("libvpx-vp9", ["-deadline", "realtime", "-cpu-used", "8"]),
                ("libaom-av1", ["-cpu-used", "8"])
            ];
        foreach ((string encoder, string[] options) in codecSmokes)
        {
            CommandResult codec = await processRunner.RunAsync(
                ffmpeg,
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-cpuflags", "0",
                    "-f", "lavfi", "-i", "color=size=64x64:rate=1",
                    "-frames:v", "1", "-c:v", encoder, .. options, "-f", "null", "-"
                ],
                payloadRoot,
                SmokeTimeout,
                environment,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(codec, "CodecSmokeFailed");
        }
    }

    private static Dictionary<string, string?> CreatePayloadEnvironment(
        string payloadRoot,
        RuntimeDefinition runtime)
    {
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
            environment["PATH"] = payloadRoot;
        }

        return environment;
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
            bool isNative = Path.GetFileName(file) is "ffmpeg" or "ffprobe" or "ffplay" ||
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
            string standardError = string.Join(
                '\n',
                result.StandardError
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .TakeLast(40));
            string standardOutput = string.Join(
                '\n',
                result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .TakeLast(20));
            string diagnostic = standardError.Length == 0
                ? standardOutput
                : standardOutput.Length == 0
                    ? standardError
                    : "stderr:\n" + standardError + "\nstdout tail:\n" + standardOutput;
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
