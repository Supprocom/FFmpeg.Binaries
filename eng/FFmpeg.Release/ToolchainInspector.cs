using System.Text;

namespace Supprocom.FFmpeg.Release;

internal sealed class ToolchainInspector(ProcessRunner processRunner)
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromMinutes(2);

    public async Task<ToolchainProvenance> InspectAndValidateAsync(
        string workingDirectory,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string actualEnvironment = runtime.Os == "linux-musl"
            ? Environment.GetEnvironmentVariable("SUPPROCOM_WORKER_IMAGE") ?? string.Empty
            : Environment.GetEnvironmentVariable("ImageVersion") ?? string.Empty;
        if (!actualEnvironment.Equals(runtime.Toolchain.EnvironmentIdentity, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException(
                "WorkerImageIdentityMismatch",
                $"Runtime Identifier '{runtime.Rid}' requires worker environment " +
                $"'{runtime.Toolchain.EnvironmentIdentity}', but the worker reported " +
                $"'{(actualEnvironment.Length == 0 ? "unidentified" : actualEnvironment)}'.");
        }

        (IReadOnlyDictionary<string, string> installed, IReadOnlyList<string> inventory) =
            await ReadInstalledPackagesAsync(workingDirectory, runtime, cancellationToken).ConfigureAwait(false);
        foreach ((string package, string expectedVersion) in runtime.Toolchain.Packages)
        {
            if (!installed.TryGetValue(package, out string? actualVersion) ||
                !actualVersion.Equals(expectedVersion, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException(
                    "ToolchainPackageVersionMismatch",
                    $"Runtime Identifier '{runtime.Rid}' requires {package}={expectedVersion}, but the worker reported " +
                    $"{(actualVersion is null ? "not installed" : package + "=" + actualVersion)}.");
            }
        }

        IReadOnlyList<string> repositoryEvidence = await CaptureRepositoryEvidenceAsync(
            workingDirectory,
            runtime,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> toolEvidence = await CaptureToolEvidenceAsync(
            workingDirectory,
            runtime,
            cancellationToken).ConfigureAwait(false);
        return new ToolchainProvenance(
            1,
            runtime.Rid,
            actualEnvironment,
            runtime.Toolchain.RepositorySnapshot,
            runtime.Toolchain.PackageManager,
            ToSortedDictionary(runtime.Toolchain.Packages),
            inventory,
            repositoryEvidence,
            toolEvidence);
    }

    private async Task<(IReadOnlyDictionary<string, string> Packages, IReadOnlyList<string> Inventory)>
        ReadInstalledPackagesAsync(
            string workingDirectory,
            RuntimeDefinition runtime,
            CancellationToken cancellationToken)
    {
        return runtime.Toolchain.PackageManager switch
        {
            "apt" => ParseTabSeparatedPackages(await RunRequiredAsync(
                "dpkg-query",
                ["-W", "-f=${Package}\\t${Version}\\n"],
                workingDirectory,
                cancellationToken).ConfigureAwait(false)),
            "apk" => await ReadApkPackagesAsync(workingDirectory, runtime, cancellationToken).ConfigureAwait(false),
            "homebrew" => await ReadHomebrewPackagesAsync(workingDirectory, runtime, cancellationToken).ConfigureAwait(false),
            "pacman" => ParseSpaceSeparatedPackages(await RunRequiredAsync(
                "pacman",
                ["-Q"],
                workingDirectory,
                cancellationToken).ConfigureAwait(false)),
            _ => throw new ReleaseFailureException(
                "UnsupportedToolchainPackageManager",
                $"Package manager '{runtime.Toolchain.PackageManager}' is unsupported.")
        };
    }

    private async Task<(IReadOnlyDictionary<string, string> Packages, IReadOnlyList<string> Inventory)>
        ReadApkPackagesAsync(
            string workingDirectory,
            RuntimeDefinition runtime,
            CancellationToken cancellationToken)
    {
        string inventoryOutput = await RunRequiredAsync(
            "apk",
            ["info", "-v"],
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var packages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string package in runtime.Toolchain.Packages.Keys)
        {
            string output = await RunRequiredAsync(
                "apk",
                ["info", "-v", package],
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            string prefix = package + "-";
            string? identity = SplitLines(output).SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
            if (identity is not null)
            {
                packages[package] = identity[prefix.Length..];
            }
        }

        return (packages, SplitLines(inventoryOutput).Order(StringComparer.Ordinal).ToArray());
    }

    private async Task<(IReadOnlyDictionary<string, string> Packages, IReadOnlyList<string> Inventory)>
        ReadHomebrewPackagesAsync(
            string workingDirectory,
            RuntimeDefinition runtime,
            CancellationToken cancellationToken)
    {
        string inventoryOutput = await RunRequiredAsync(
            "brew",
            ["list", "--versions"],
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        (IReadOnlyDictionary<string, string> packages, IReadOnlyList<string> inventory) =
            ParseSpaceSeparatedPackages(inventoryOutput);
        SortedDictionary<string, string> complete = ToSortedDictionary(packages);
        string xcodeOutput = await RunRequiredAsync(
            "xcodebuild",
            ["-version"],
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        string[] xcodeLines = SplitLines(xcodeOutput);
        if (xcodeLines.Length == 2 &&
            xcodeLines[0].StartsWith("Xcode ", StringComparison.Ordinal) &&
            xcodeLines[1].StartsWith("Build version ", StringComparison.Ordinal))
        {
            complete["xcode"] = xcodeLines[0]["Xcode ".Length..] +
                " (" + xcodeLines[1]["Build version ".Length..] + ")";
        }

        return (complete, [.. inventory, $"xcode={complete.GetValueOrDefault("xcode", "unidentified")}"]);
    }

    private async Task<IReadOnlyList<string>> CaptureRepositoryEvidenceAsync(
        string workingDirectory,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        var evidence = new List<string>
        {
            "approved-snapshot: " + runtime.Toolchain.RepositorySnapshot
        };
        switch (runtime.Toolchain.PackageManager)
        {
            case "apt":
                AddTextFileEvidence(evidence, "/etc/apt/sources.list");
                if (Directory.Exists("/etc/apt/sources.list.d"))
                {
                    foreach (string path in Directory.EnumerateFiles("/etc/apt/sources.list.d", "*", SearchOption.TopDirectoryOnly)
                                 .Order(StringComparer.Ordinal))
                    {
                        AddTextFileEvidence(evidence, path);
                    }
                }

                evidence.Add(await CaptureCommandAsync(
                    "apt-policy",
                    "apt-cache",
                    ["policy"],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false));
                break;
            case "apk":
                AddTextFileEvidence(evidence, "/etc/apk/repositories");
                evidence.Add(await CaptureCommandAsync(
                    "apk-policy",
                    "apk",
                    ["policy"],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false));
                break;
            case "homebrew":
                evidence.Add(await CaptureCommandAsync(
                    "brew-config",
                    "brew",
                    ["config"],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false));
                evidence.Add(await CaptureCommandAsync(
                    "macos-sdk",
                    "xcrun",
                    ["--show-sdk-path"],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false));
                break;
            case "pacman":
                evidence.Add(await CaptureCommandAsync(
                    "pacman-repositories",
                    "pacman-conf",
                    ["--repo-list"],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false));
                evidence.Add(await CaptureCommandAsync(
                    "pacman-config",
                    "pacman-conf",
                    [],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false));
                break;
        }

        return evidence;
    }

    private async Task<IReadOnlyList<string>> CaptureToolEvidenceAsync(
        string workingDirectory,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        (string Label, string Tool, string[] Arguments)[] commands = runtime.Os switch
        {
            "linux" or "linux-musl" =>
            [
                ("compiler", "gcc", ["--version"]),
                ("assembler", "nasm", ["-v"]),
                ("linker", "ld", ["--version"]),
                ("strip", "strip", ["--version"]),
                ("patchelf", "patchelf", ["--version"]),
                ("make", "make", ["--version"]),
                ("file", "file", ["--version"])
            ],
            "macos" =>
            [
                ("compiler", "clang", ["--version"]),
                ("assembler", "xcrun", ["--find", "as"]),
                ("linker", "xcrun", ["--find", "ld"]),
                ("strip", "xcrun", ["--find", "strip"]),
                ("nasm", "nasm", ["-v"]),
                ("make", "make", ["--version"]),
                ("xcode", "xcodebuild", ["-version"])
            ],
            "windows" when runtime.Architecture == "arm64" =>
            [
                ("compiler", "clang", ["--version"]),
                ("assembler", "llvm-mc", ["--version"]),
                ("archiver", "llvm-ar", ["--version"]),
                ("linker", "ld.lld", ["--version"]),
                ("strip", "llvm-strip", ["--version"]),
                ("make", "make", ["--version"]),
                ("file", "file", ["--version"])
            ],
            "windows" =>
            [
                ("compiler", "gcc", ["--version"]),
                ("assembler", "as", ["--version"]),
                ("nasm", "nasm", ["-v"]),
                ("linker", "ld", ["--version"]),
                ("strip", "strip", ["--version"]),
                ("make", "make", ["--version"]),
                ("file", "file", ["--version"])
            ],
            _ => throw new ReleaseFailureException(
                "UnsupportedWorkerOS",
                $"Worker OS '{runtime.Os}' is unsupported.")
        };
        var evidence = new List<string>(commands.Length);
        foreach ((string label, string tool, string[] arguments) in commands)
        {
            evidence.Add(await CaptureCommandAsync(
                label,
                tool,
                arguments,
                workingDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        return evidence;
    }

    private async Task<string> RunRequiredAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            tool,
            arguments,
            workingDirectory,
            InspectionTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ReleaseFailureException(
                "ToolchainInspectionFailed",
                $"Required toolchain inspection command '{tool}' exited with code {result.ExitCode}.");
        }

        return result.StandardOutput + result.StandardError;
    }

    private async Task<string> CaptureCommandAsync(
        string label,
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            tool,
            arguments,
            workingDirectory,
            InspectionTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        string[] lines = SplitLines(result.StandardOutput + result.StandardError);
        if (lines.Length == 0)
        {
            throw new ReleaseFailureException(
                "ToolchainInspectionFailed",
                $"Required toolchain inspection command '{tool}' produced no identity evidence.");
        }

        return $"{label} ({tool}, exit {result.ExitCode}): {string.Join(" | ", lines)}";
    }

    private static (IReadOnlyDictionary<string, string> Packages, IReadOnlyList<string> Inventory)
        ParseTabSeparatedPackages(string output)
    {
        var packages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in SplitLines(output))
        {
            string[] fields = line.Split('\t', 2);
            if (fields.Length == 2)
            {
                packages[fields[0]] = fields[1];
            }
        }

        return (packages, packages.Select(item => $"{item.Key}={item.Value}").ToArray());
    }

    private static (IReadOnlyDictionary<string, string> Packages, IReadOnlyList<string> Inventory)
        ParseSpaceSeparatedPackages(string output)
    {
        var packages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in SplitLines(output))
        {
            string[] fields = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2)
            {
                packages[fields[0]] = fields[1];
            }
        }

        return (packages, packages.Select(item => $"{item.Key}={item.Value}").ToArray());
    }

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static SortedDictionary<string, string> ToSortedDictionary(
        IReadOnlyDictionary<string, string> source)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in source)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static void AddTextFileEvidence(List<string> evidence, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string content = string.Join(
            " | ",
            SplitLines(File.ReadAllText(path, Encoding.UTF8))
                .Where(line => !line.StartsWith('#')));
        evidence.Add($"{path}: {content}");
    }
}
