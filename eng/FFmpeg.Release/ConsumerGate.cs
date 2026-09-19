using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

namespace Supprocom.FFmpeg.Release;

internal sealed class ConsumerGate(ProcessRunner processRunner)
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExecuteTimeout = TimeSpan.FromMinutes(2);
    private const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    public async Task RunAsync(
        string repositoryRoot,
        ReleaseMatrix matrix,
        string matrixHash,
        string releaseCommit,
        VersionDefinition version,
        RuntimeDefinition runtime,
        string packageRoot,
        string testRoot,
        string? consumerOutputRoot,
        CancellationToken cancellationToken)
    {
        ValidateHost(runtime);
        string packages = Path.GetFullPath(packageRoot);
        FrozenReleaseManifest release = await ValidateFrozenSetAsync(
            matrix,
            matrixHash,
            releaseCommit,
            version,
            packages,
            cancellationToken).ConfigureAwait(false);

        string root = PrepareTestRoot(repositoryRoot, testRoot, runtime.Rid);
        var scenarios = new List<string>();
        string cache = Path.Combine(root, "nuget-cache");
        Directory.CreateDirectory(cache);
        string nugetConfig = WriteNuGetConfig(root, packages);
        var environment = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = cache,
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["SUPPROCOM_TEST_RID"] = runtime.Rid,
            ["SUPPROCOM_NUGET_CONFIG"] = nugetConfig
        };

        string runtimePackage = $"Supprocom.FFmpeg.Binaries.{runtime.Rid}";
        await PublishAndRunAsync(
            repositoryRoot,
            packages,
            root,
            environment,
            runtime,
            version.Version,
            runtimePackage,
            "runtime-self-contained",
            "net10.0",
            selfContained: true,
            [],
            cancellationToken).ConfigureAwait(false);
        scenarios.Add("runtime-self-contained-net10.0");
        await PublishAndRunAsync(
            repositoryRoot,
            packages,
            root,
            environment,
            runtime,
            version.Version,
            "Supprocom.FFmpeg.Binaries",
            "facade-framework-dependent",
            "net10.0",
            selfContained: false,
            [],
            cancellationToken).ConfigureAwait(false);
        scenarios.Add("facade-framework-dependent-net10.0");

        await RunWrapperAsync(
            repositoryRoot,
            packages,
            root,
            environment,
            runtime,
            version.Version,
            "FFMpegCoreConsumer",
            cancellationToken).ConfigureAwait(false);
        scenarios.Add("ffmpegcore-5.4.0");
        await RunWrapperAsync(
            repositoryRoot,
            packages,
            root,
            environment,
            runtime,
            version.Version,
            "XabeConsumer",
            cancellationToken).ConfigureAwait(false);
        scenarios.Add("xabe-ffmpeg-6.0.2");

        if (runtime.Rid == "linux-x64")
        {
            await PublishAndRunAsync(
                repositoryRoot,
                packages,
                root,
                environment,
                runtime,
                version.Version,
                "Supprocom.FFmpeg.Binaries",
                "facade-net8-self-contained",
                "net8.0",
                selfContained: true,
                [],
                cancellationToken).ConfigureAwait(false);
            scenarios.Add("facade-self-contained-net8.0");
            await PublishAndRunAsync(
                repositoryRoot,
                packages,
                root,
                environment,
                runtime,
                version.Version,
                "Supprocom.FFmpeg.Binaries",
                "facade-trimmed",
                "net10.0",
                selfContained: true,
                ["-p:PublishTrimmed=true"],
                cancellationToken).ConfigureAwait(false);
            scenarios.Add("facade-trimmed-net10.0");
            await PublishAndRunAsync(
                repositoryRoot,
                packages,
                root,
                environment,
                runtime,
                version.Version,
                "Supprocom.FFmpeg.Binaries",
                "facade-single-file",
                "net10.0",
                selfContained: true,
                ["-p:PublishSingleFile=true"],
                cancellationToken).ConfigureAwait(false);
            scenarios.Add("facade-single-file-net10.0");
        }

        if (runtime.Rid == "win-x64")
        {
            await BuildNetFrameworkAsync(
                repositoryRoot,
                packages,
                root,
                environment,
                runtime,
                version.Version,
                cancellationToken).ConfigureAwait(false);
            scenarios.Add("sdk-style-net48-build");
        }

        string releaseManifestPath = Path.Combine(packages, "release-manifest.json");
        var attestation = new ConsumerAttestation(
            1,
            release.PlanSha256,
            await HashFileAsync(releaseManifestPath, cancellationToken).ConfigureAwait(false),
            version.Version,
            runtime.Rid,
            scenarios,
            DateTimeOffset.UtcNow);
        WriteAttestation(
            repositoryRoot,
            consumerOutputRoot ?? Path.Combine(root, "attestations"),
            attestation);

        Console.WriteLine(
            $"Consumer gate passed for {runtime.Rid}: {release.Packages.Count} frozen packages, direct package, facade, and wrapper integrations.");
    }

    private async Task PublishAndRunAsync(
        string repositoryRoot,
        string packages,
        string root,
        Dictionary<string, string?> environment,
        RuntimeDefinition runtime,
        string version,
        string packageId,
        string scenario,
        string framework,
        bool selfContained,
        IReadOnlyList<string> extraPublishArguments,
        CancellationToken cancellationToken)
    {
        string projectRoot = CopyFixture(repositoryRoot, root, "PackageConsumer", scenario);
        string project = Path.Combine(projectRoot, "PackageConsumer.csproj");
        string output = Path.Combine(root, "publish", scenario);
        IReadOnlyList<string> properties =
        [
            $"-p:TestRid={runtime.Rid}",
            $"-p:SupprocomPackageId={packageId}",
            $"-p:SupprocomPackageVersion={version}",
            $"-p:SupprocomFFmpegRuntimeIdentifier={runtime.Rid}",
            $"-p:TargetFramework={framework}"
        ];
        var restoreArguments = new List<string> { "restore", project };
        if (selfContained)
        {
            restoreArguments.AddRange(["--runtime", runtime.Rid]);
        }

        restoreArguments.AddRange(
        [
            "--packages", environment["NUGET_PACKAGES"]!,
            "--configfile", environment["SUPPROCOM_NUGET_CONFIG"]!,
            .. properties,
            "--nologo"
        ]);
        await RunDotnetAsync(
            restoreArguments,
            projectRoot,
            RestoreTimeout,
            environment,
            "ConsumerRestoreFailed",
            cancellationToken).ConfigureAwait(false);
        var publishArguments = new List<string>
        {
            "publish", project,
            "--configuration", "Release",
            "--framework", framework
        };
        if (selfContained)
        {
            publishArguments.AddRange(["--runtime", runtime.Rid]);
        }

        publishArguments.AddRange(
        [
            "--self-contained", selfContained ? "true" : "false",
            "--no-restore",
            "--output", output,
            .. properties,
            .. extraPublishArguments,
            "--nologo"
        ]);
        await RunDotnetAsync(
            publishArguments,
            projectRoot,
            BuildTimeout,
            environment,
            "ConsumerPublishFailed",
            cancellationToken).ConfigureAwait(false);
        ValidatePublishedLayout(output, runtime.Rid);

        string app = Path.Combine(output, OperatingSystem.IsWindows() ? "PackageConsumer.exe" : "PackageConsumer");
        IReadOnlyList<string> executableArguments;
        if (selfContained)
        {
            executableArguments = [];
        }
        else
        {
            app = "dotnet";
            executableArguments = [Path.Combine(output, "PackageConsumer.dll")];
        }

        CommandResult result = await processRunner.RunAsync(
            app,
            executableArguments,
            output,
            ExecuteTimeout,
            environment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "ConsumerExecutionFailed");
    }

    private async Task RunWrapperAsync(
        string repositoryRoot,
        string packages,
        string root,
        Dictionary<string, string?> environment,
        RuntimeDefinition runtime,
        string version,
        string fixture,
        CancellationToken cancellationToken)
    {
        string scenario = fixture;
        string projectRoot = CopyFixture(repositoryRoot, root, fixture, scenario);
        string project = Path.Combine(projectRoot, fixture + ".csproj");
        IReadOnlyList<string> properties =
        [
            $"-p:TestRid={runtime.Rid}",
            $"-p:SupprocomPackageVersion={version}",
            $"-p:SupprocomFFmpegRuntimeIdentifier={runtime.Rid}"
        ];
        await RunDotnetAsync(
            [
                "restore", project,
                "--packages", environment["NUGET_PACKAGES"]!,
                "--configfile", environment["SUPPROCOM_NUGET_CONFIG"]!,
                .. properties,
                "--nologo"
            ],
            projectRoot,
            RestoreTimeout,
            environment,
            "WrapperRestoreFailed",
            cancellationToken).ConfigureAwait(false);
        CommandResult result = await processRunner.RunAsync(
            "dotnet",
            [
                "run", "--project", project,
                "--configuration", "Release",
                "--no-restore",
                .. properties,
                "--nologo"
            ],
            projectRoot,
            BuildTimeout,
            environment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "WrapperExecutionFailed");
    }

    private async Task BuildNetFrameworkAsync(
        string repositoryRoot,
        string packages,
        string root,
        Dictionary<string, string?> environment,
        RuntimeDefinition runtime,
        string version,
        CancellationToken cancellationToken)
    {
        const string scenario = "net48-sdk-style";
        string projectRoot = CopyFixture(repositoryRoot, root, "PackageConsumer", scenario);
        string project = Path.Combine(projectRoot, "PackageConsumer.csproj");
        IReadOnlyList<string> properties =
        [
            $"-p:TestRid={runtime.Rid}",
            $"-p:SupprocomPackageVersion={version}",
            $"-p:SupprocomFFmpegRuntimeIdentifier={runtime.Rid}",
            "-p:TargetFramework=net48"
        ];
        await RunDotnetAsync(
            [
                "restore", project,
                "--packages", environment["NUGET_PACKAGES"]!,
                "--configfile", environment["SUPPROCOM_NUGET_CONFIG"]!,
                .. properties,
                "--nologo"
            ],
            projectRoot,
            RestoreTimeout,
            environment,
            "NetFrameworkRestoreFailed",
            cancellationToken).ConfigureAwait(false);
        await RunDotnetAsync(
            ["build", project, "--configuration", "Release", "--no-restore", .. properties, "--nologo"],
            projectRoot,
            BuildTimeout,
            environment,
            "NetFrameworkBuildFailed",
            cancellationToken).ConfigureAwait(false);
        ValidatePublishedLayout(Path.Combine(projectRoot, "bin", "Release", "net48"), runtime.Rid);
    }

    private static async Task<FrozenReleaseManifest> ValidateFrozenSetAsync(
        ReleaseMatrix matrix,
        string matrixHash,
        string releaseCommit,
        VersionDefinition version,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(packageRoot, "release-manifest.json");
        if (!Directory.Exists(packageRoot) || !File.Exists(manifestPath))
        {
            throw new ReleaseFailureException("FrozenPackageSetMissing", "The complete frozen package set is missing.");
        }

        FrozenReleaseManifest manifest = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            ReleaseJsonContext.Default.FrozenReleaseManifest)
            ?? throw new ReleaseFailureException("FrozenManifestInvalid", "The frozen release manifest is empty.");
        int expectedCount = matrix.RuntimeIdentifiers.Count + 3;
        if (!manifest.CompleteRuntimeMatrix ||
            !manifest.Version.Equals(version.Version, StringComparison.Ordinal) ||
            !manifest.MatrixSha256.Equals(matrixHash, StringComparison.Ordinal) ||
            !manifest.ReleaseProgramCommit.Equals(releaseCommit, StringComparison.Ordinal) ||
            manifest.Packages.Count != expectedCount)
        {
            throw new ReleaseFailureException("FrozenPackageSetIncomplete", "Consumer gates require the complete exact-version package family.");
        }

        string sbomPath = Path.Combine(packageRoot, manifest.SbomFileName);
        if (!Path.GetFileName(manifest.SbomFileName).Equals(manifest.SbomFileName, StringComparison.Ordinal) ||
            !File.Exists(sbomPath) ||
            !(await HashFileAsync(sbomPath, cancellationToken).ConfigureAwait(false))
                .Equals(manifest.SbomSha256, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("ReleaseSbomInvalid", "The frozen release SBOM is missing or changed.");
        }

        foreach (FrozenPackage package in manifest.Packages)
        {
            if (!Path.GetFileName(package.FileName).Equals(package.FileName, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException("FrozenPackagePathInvalid", "A frozen package manifest contains an unsafe file name.");
            }

            string path = Path.Combine(packageRoot, package.FileName);
            if (!File.Exists(path) || new FileInfo(path).Length != package.Size)
            {
                throw new ReleaseFailureException("FrozenPackageMissing", $"Frozen package '{package.Id}' is absent or has changed size.");
            }

            string hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!hash.Equals(package.Sha256, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException("FrozenPackageHashMismatch", $"Frozen package '{package.Id}' changed after assembly.");
            }
        }

        return manifest;
    }

    private static string PrepareTestRoot(string repositoryRoot, string configuredRoot, string runtimeIdentifier)
    {
        string baseRoot = Path.GetFullPath(configuredRoot);
        string root = Path.GetFullPath(Path.Combine(baseRoot, runtimeIdentifier));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string repositoryPrefix = Path.GetFullPath(repositoryRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((root + Path.DirectorySeparatorChar).StartsWith(repositoryPrefix, comparison))
        {
            throw new ReleaseFailureException("ConsumerRootInsideRepository", "Consumer test output must be outside the source checkout.");
        }

        string basePrefix = baseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!(root + Path.DirectorySeparatorChar).StartsWith(basePrefix, comparison))
        {
            throw new ReleaseFailureException("UnsafeConsumerRoot", "The consumer test directory escaped its configured root.");
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteNuGetConfig(string root, string packages)
    {
        string path = Path.Combine(root, "NuGet.Config");
        var settings = new XmlWriterSettings
        {
            Indent = true,
            NewLineChars = "\n"
        };
        using (XmlWriter writer = XmlWriter.Create(path, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("configuration");
            writer.WriteStartElement("packageSources");
            writer.WriteStartElement("clear");
            writer.WriteEndElement();
            writer.WriteStartElement("add");
            writer.WriteAttributeString("key", "frozen-packages");
            writer.WriteAttributeString("value", packages);
            writer.WriteEndElement();
            writer.WriteStartElement("add");
            writer.WriteAttributeString("key", "nuget.org");
            writer.WriteAttributeString("value", NuGetOrg);
            writer.WriteAttributeString("protocolVersion", "3");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return path;
    }

    private static void WriteAttestation(
        string repositoryRoot,
        string configuredRoot,
        ConsumerAttestation attestation)
    {
        string root = Path.GetFullPath(configuredRoot);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string repositoryPrefix = Path.GetFullPath(repositoryRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(repositoryPrefix, comparison))
        {
            throw new ReleaseFailureException("ConsumerAttestationInsideRepository", "Consumer attestations must be written outside the source checkout.");
        }

        Directory.CreateDirectory(root);
        string path = Path.Combine(root, attestation.RuntimeIdentifier + ".json");
        if (File.Exists(path))
        {
            throw new ReleaseFailureException("ConsumerAttestationExists", $"A consumer attestation already exists for {attestation.RuntimeIdentifier}.");
        }

        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(attestation, ReleaseJsonContext.Default.ConsumerAttestation));
    }

    private static string CopyFixture(string repositoryRoot, string root, string fixture, string scenario)
    {
        string source = Path.Combine(repositoryRoot, "tests", fixture);
        string destination = Path.Combine(root, "projects", scenario);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }

        return destination;
    }

    private async Task RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Dictionary<string, string?> environment,
        string failureCode,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            "dotnet",
            arguments,
            workingDirectory,
            timeout,
            environment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, failureCode);
    }

    private static void ValidatePublishedLayout(string output, string runtimeIdentifier)
    {
        string payload = Path.Combine(output, "ffmpeg");
        string marker = Path.Combine(payload, "runtime-identifier.txt");
        if (!Directory.Exists(payload) ||
            !File.Exists(marker) ||
            !File.ReadAllText(marker).Trim().Equals(runtimeIdentifier, StringComparison.Ordinal) ||
            Directory.Exists(Path.Combine(output, "runtimes")))
        {
            throw new ReleaseFailureException("ConsumerLayoutInvalid", $"The published output did not isolate the {runtimeIdentifier} payload.");
        }
    }

    private static void ValidateHost(RuntimeDefinition runtime)
    {
        bool matchesOs = runtime.Os switch
        {
            "windows" => OperatingSystem.IsWindows(),
            "macos" => OperatingSystem.IsMacOS(),
            "linux" or "linux-musl" => OperatingSystem.IsLinux(),
            _ => false
        };
        bool matchesLibc = runtime.Os switch
        {
            "linux-musl" => File.Exists("/etc/alpine-release"),
            "linux" => !File.Exists("/etc/alpine-release"),
            _ => true
        };
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        bool matchesArchitecture = runtime.Architecture switch
        {
            "x86" => OperatingSystem.IsWindows() &&
                (architecture == Architecture.X86 || architecture == Architecture.X64),
            "x64" => architecture == Architecture.X64,
            "arm64" => architecture == Architecture.Arm64,
            _ => false
        };
        if (!matchesOs || !matchesLibc || !matchesArchitecture)
        {
            throw new ReleaseFailureException("ConsumerHostMismatch", $"The current host cannot execute the {runtime.Rid} consumer gate.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void EnsureSuccess(CommandResult result, string code)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        string diagnostic = string.Join(
            '\n',
            (result.StandardError + "\n" + result.StandardOutput)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(30));
        throw new ReleaseFailureException(code, $"A package-consumer command failed.\n{diagnostic}");
    }
}
