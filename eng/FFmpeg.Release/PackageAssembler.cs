using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Supprocom.FFmpeg.Release;

internal sealed class PackageAssembler(ProcessRunner processRunner)
{
    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] RequiredSourceLicenseEntries =
    [
        "licenses/FFmpeg-LICENSE.md",
        "licenses/COPYING.GPLv2",
        "licenses/COPYING.GPLv3",
        "licenses/COPYING.LGPLv2.1",
        "licenses/COPYING.LGPLv3"
    ];
    private const long MaximumPackageBytes = 250L * 1024 * 1024;

    public async Task<string> AssembleAsync(
        string repositoryRoot,
        ReleasePlan plan,
        string planHash,
        string planDirectory,
        string? workerArtifactRoot,
        FlavorDefinition flavor,
        SourceVerificationResult source,
        IReadOnlyList<RuntimeDefinition> runtimes,
        bool completeRuntimeMatrix,
        CancellationToken cancellationToken)
    {
        if (completeRuntimeMatrix && runtimes.Count != plan.RuntimeIdentifiers.Count)
        {
            throw new ReleaseFailureException("IncompletePackageMatrix", "Stable package assembly requires every approved Runtime Identifier.");
        }

        string packageWork = Path.Combine(planDirectory, "package-work");
        string candidateDirectory = Path.Combine(packageWork, "candidates");
        string frozenDirectory = Path.Combine(planDirectory, "frozen");
        RecreateOwnedDirectory(packageWork, planDirectory);
        Directory.CreateDirectory(candidateDirectory);
        Directory.CreateDirectory(frozenDirectory);
        var frozen = new List<FrozenPackage>();

        string sourceCandidate = await PackSourceAsync(
            repositoryRoot,
            plan,
            planHash,
            planDirectory,
            workerArtifactRoot,
            flavor,
            source,
            runtimes,
            packageWork,
            candidateDirectory,
            cancellationToken).ConfigureAwait(false);
        frozen.Add(await InspectAndFreezeAsync(
            sourceCandidate,
            flavor.SourcePackageId,
            plan.PackageVersion,
            "source",
            frozenDirectory,
            cancellationToken).ConfigureAwait(false));

        string coreCandidate = await PackCoreAsync(
            repositoryRoot,
            plan,
            candidateDirectory,
            cancellationToken).ConfigureAwait(false);
        frozen.Add(await InspectAndFreezeAsync(
            coreCandidate,
            flavor.CorePackageId,
            plan.PackageVersion,
            "core",
            frozenDirectory,
            cancellationToken).ConfigureAwait(false));

        foreach (RuntimeDefinition runtime in runtimes.OrderBy(item => item.Rid, StringComparer.Ordinal))
        {
            (WorkerManifest manifest, string payload) = await ReadAndValidateWorkerAsync(
                plan,
                planHash,
                planDirectory,
                workerArtifactRoot,
                runtime,
                cancellationToken).ConfigureAwait(false);
            string runtimeCandidate = await PackRuntimeAsync(
                repositoryRoot,
                plan,
                flavor,
                runtime,
                manifest,
                payload,
                packageWork,
                candidateDirectory,
                cancellationToken).ConfigureAwait(false);
            frozen.Add(await InspectAndFreezeAsync(
                runtimeCandidate,
                $"{flavor.FacadePackageId}.{runtime.Rid}",
                plan.PackageVersion,
                "runtime",
                frozenDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        if (completeRuntimeMatrix)
        {
            string facadeCandidate = await PackFacadeAsync(
                repositoryRoot,
                plan,
                flavor,
                runtimes,
                packageWork,
                candidateDirectory,
                cancellationToken).ConfigureAwait(false);
            frozen.Add(await InspectAndFreezeAsync(
                facadeCandidate,
                flavor.FacadePackageId,
                plan.PackageVersion,
                "facade",
                frozenDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        const string sbomFileName = "release.spdx.json";
        string sbomPath = Path.Combine(frozenDirectory, sbomFileName);
        await WriteReleaseSbomAsync(sbomPath, plan, frozen, cancellationToken).ConfigureAwait(false);
        string sbomHash = await HashFileAsync(sbomPath, cancellationToken).ConfigureAwait(false);
        var releaseManifest = new FrozenReleaseManifest(
            1,
            planHash,
            plan.PackageVersion,
            plan.ReleaseProgramCommit,
            plan.MatrixSha256,
            completeRuntimeMatrix,
            sbomFileName,
            sbomHash,
            frozen,
            DateTimeOffset.UtcNow);
        string manifestPath = Path.Combine(frozenDirectory, "release-manifest.json");
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(releaseManifest, ReleaseJsonContext.Default.FrozenReleaseManifest),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            Path.Combine(frozenDirectory, "SHA256SUMS"),
            frozen.Select(item => $"{item.Sha256}  {item.FileName}")
                .Append($"{sbomHash}  {sbomFileName}"),
            Encoding.ASCII,
            cancellationToken).ConfigureAwait(false);
        string journalPath = Path.Combine(frozenDirectory, "publication-journal.jsonl");
        if (!File.Exists(journalPath))
        {
            await File.WriteAllTextAsync(
                journalPath,
                JsonSerializer.Serialize(new { type = "frozen", planSha256 = planHash, packageCount = frozen.Count }) + "\n",
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
        }

        RecreateOwnedDirectory(packageWork, planDirectory);
        Directory.Delete(packageWork);
        return manifestPath;
    }

    private static async Task WriteReleaseSbomAsync(
        string path,
        ReleasePlan plan,
        IReadOnlyList<FrozenPackage> packages,
        CancellationToken cancellationToken)
    {
        object[] spdxPackages = packages.Select(package =>
        {
            bool isCompleteSource = package.Kind.Equals("source", StringComparison.Ordinal);
            return (object)new
            {
                name = package.Id,
                SPDXID = "SPDXRef-Package-" + package.Id,
                versionInfo = package.Version,
                downloadLocation = "NOASSERTION",
                filesAnalyzed = false,
                licenseConcluded = isCompleteSource ? "NOASSERTION" : "LGPL-2.1-or-later",
                licenseDeclared = isCompleteSource ? "NOASSERTION" : "LGPL-2.1-or-later",
                licenseComments = isCompleteSource
                    ? "Complete upstream FFmpeg source archive with file-specific LGPL-2.1-or-later, GPL-2.0-or-later, GPL-3.0-or-later, MIT, BSD, Expat, and IJG terms. See licenses/FFmpeg-LICENSE.md, the four COPYING files, and notices in the source archive."
                    : "License claim is scoped to the LGPL-configured binary/package family built with GPL and nonfree code disabled.",
                copyrightText = "NOASSERTION",
                checksums = new[]
                {
                    new { algorithm = "SHA256", checksumValue = package.Sha256 }
                }
            };
        }).ToArray();
        object[] relationships = packages.Select(package => (object)new
        {
            spdxElementId = "SPDXRef-DOCUMENT",
            relationshipType = "DESCRIBES",
            relatedSpdxElement = "SPDXRef-Package-" + package.Id
        }).ToArray();
        var document = new
        {
            spdxVersion = "SPDX-2.3",
            dataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            name = $"Supprocom.FFmpeg.Binaries-{plan.PackageVersion}",
            documentNamespace = $"https://github.com/Supprocom/FFmpeg.Binaries/releases/{plan.PackageVersion}/{plan.ReleaseProgramCommit}/sbom",
            creationInfo = new
            {
                created = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                creators = new[] { "Tool: Supprocom.FFmpeg.Release-1.0" }
            },
            packages = spdxPackages,
            relationships
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, IndentedJson) + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PackCoreAsync(
        string repositoryRoot,
        ReleasePlan plan,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        string project = Path.Combine(
            repositoryRoot,
            "src",
            "Supprocom.FFmpeg.Binaries.Core",
            "Supprocom.FFmpeg.Binaries.Core.csproj");
        CommandResult result = await processRunner.RunAsync(
            "dotnet",
            [
                "pack", project,
                "--configuration", "Release",
                "--output", candidateDirectory,
                $"-p:Version={plan.PackageVersion}",
                $"-p:RepositoryCommit={plan.ReleaseProgramCommit}",
                "-p:IncludeSymbols=false",
                "--nologo"
            ],
            repositoryRoot,
            PackTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "CorePackageCreationFailed");
        return RequireSingleCandidate(candidateDirectory, $"Supprocom.FFmpeg.Binaries.Core.{plan.PackageVersion}.nupkg");
    }

    private async Task<string> PackRuntimeAsync(
        string repositoryRoot,
        ReleasePlan plan,
        FlavorDefinition flavor,
        RuntimeDefinition runtime,
        WorkerManifest manifest,
        string payload,
        string packageWork,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        string packageId = $"{flavor.FacadePackageId}.{runtime.Rid}";
        string root = Path.Combine(packageWork, packageId);
        Directory.CreateDirectory(root);
        CopyTree(payload, Path.Combine(root, "payload"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "payload", "runtime-identifier.txt"),
            runtime.Rid + "\n",
            Encoding.ASCII,
            cancellationToken).ConfigureAwait(false);
        File.Copy(Path.Combine(repositoryRoot, "packaging", "README.md"), Path.Combine(root, "README.md"));
        string template = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "packaging", "runtime", "Supprocom.FFmpeg.Binaries.Runtime.props.in"),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "runtime.props"),
            template.Replace("@RID@", runtime.Rid, StringComparison.Ordinal),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "worker-manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, ReleaseJsonContext.Default.WorkerManifest),
            cancellationToken).ConfigureAwait(false);
        string nuspec = CreateNuspec(
            packageId,
            plan,
            "Native FFmpeg and FFprobe payload for " + runtime.Rid + ".",
            [(flavor.CorePackageId, plan.PackageVersion)],
            [
                ("payload/**/*", $"runtimes/{runtime.Rid}/native/ffmpeg"),
                ("runtime.props", $"buildTransitive/{packageId}.props"),
                ("worker-manifest.json", "build-metadata"),
                ("README.md", string.Empty)
            ]);
        return await PackNuspecAsync(root, nuspec, packageId, plan.PackageVersion, candidateDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> PackSourceAsync(
        string repositoryRoot,
        ReleasePlan plan,
        string planHash,
        string planDirectory,
        string? workerArtifactRoot,
        FlavorDefinition flavor,
        SourceVerificationResult source,
        IReadOnlyList<RuntimeDefinition> runtimes,
        string packageWork,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(packageWork, flavor.SourcePackageId);
        Directory.CreateDirectory(Path.Combine(root, "source"));
        Directory.CreateDirectory(Path.Combine(root, "build"));
        Directory.CreateDirectory(Path.Combine(root, "licenses"));
        Directory.CreateDirectory(Path.Combine(root, "provenance", "workers"));
        Directory.CreateDirectory(Path.Combine(root, "provenance", "toolchains"));
        Directory.CreateDirectory(Path.Combine(root, "patches"));
        File.Copy(source.ArchivePath, Path.Combine(root, "source", Path.GetFileName(source.ArchivePath)));
        File.Copy(source.SignaturePath, Path.Combine(root, "source", Path.GetFileName(source.SignaturePath)));
        File.Copy(source.ReleaseKeyPath, Path.Combine(root, "source", Path.GetFileName(source.ReleaseKeyPath)));
        CopyTree(Path.Combine(repositoryRoot, "eng", "FFmpeg.Release"), Path.Combine(root, "build", "FFmpeg.Release"));
        File.Copy(Path.Combine(repositoryRoot, "eng", "release-matrix.json"), Path.Combine(root, "build", "release-matrix.json"));
        File.Copy(Path.Combine(repositoryRoot, "FFmpeg.Release.csproj"), Path.Combine(root, "build", "FFmpeg.Release.csproj"));
        File.Copy(Path.Combine(repositoryRoot, "global.json"), Path.Combine(root, "build", "global.json"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "patches", "README.txt"),
            "No FFmpeg source patch is applied by this package version.\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "provenance", "plan-sha256.txt"),
            planHash + "\n",
            Encoding.ASCII,
            cancellationToken).ConfigureAwait(false);
        foreach (RuntimeDefinition runtime in runtimes)
        {
            string accepted = ResolveAcceptedWorkerDirectory(planDirectory, workerArtifactRoot, runtime.Rid);
            string manifest = Path.Combine(accepted, "worker-manifest.json");
            string toolchain = Path.Combine(
                accepted,
                "payload",
                "BUILD-METADATA",
                "toolchain-provenance.json");
            if (!File.Exists(manifest) || !File.Exists(toolchain))
            {
                throw new ReleaseFailureException(
                    "SourceProvenanceIncomplete",
                    $"The {runtime.Rid} source provenance is missing its worker manifest or toolchain inventory.");
            }

            File.Copy(manifest, Path.Combine(root, "provenance", "workers", runtime.Rid + ".json"));
            File.Copy(toolchain, Path.Combine(root, "provenance", "toolchains", runtime.Rid + ".json"));
        }

        string archiveRoot = $"ffmpeg-{plan.Version}";
        CommandResult licenseExtraction = await processRunner.RunAsync(
            "tar",
            [
                "-xJf", source.ArchivePath,
                "--strip-components=1",
                "-C", Path.Combine(root, "licenses"),
                $"{archiveRoot}/COPYING.GPLv2",
                $"{archiveRoot}/COPYING.GPLv3",
                $"{archiveRoot}/COPYING.LGPLv2.1",
                $"{archiveRoot}/COPYING.LGPLv3",
                $"{archiveRoot}/LICENSE.md"
            ],
            root,
            TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(licenseExtraction, "SourceLicenseExtractionFailed");
        File.Move(
            Path.Combine(root, "licenses", "LICENSE.md"),
            Path.Combine(root, "licenses", "FFmpeg-LICENSE.md"));
        File.Copy(Path.Combine(repositoryRoot, "packaging", "SOURCE-README.md"), Path.Combine(root, "README.md"));
        string nuspec = CreateNuspec(
            flavor.SourcePackageId,
            plan,
            "Exact corresponding FFmpeg source, release verification, build definitions, license material, and provenance.",
            [],
            [
                ("source/**/*", "source"),
                ("build/**/*", "build"),
                ("provenance/**/*", "provenance"),
                ("patches/**/*", "patches"),
                ("licenses/COPYING.GPLv2", "licenses"),
                ("licenses/COPYING.GPLv3", "licenses"),
                ("licenses/COPYING.LGPLv2.1", "licenses"),
                ("licenses/COPYING.LGPLv3", "licenses"),
                ("licenses/FFmpeg-LICENSE.md", "licenses"),
                ("README.md", string.Empty)
            ],
            licenseFile: "licenses/FFmpeg-LICENSE.md");
        return await PackNuspecAsync(
            root,
            nuspec,
            flavor.SourcePackageId,
            plan.PackageVersion,
            candidateDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PackFacadeAsync(
        string repositoryRoot,
        ReleasePlan plan,
        FlavorDefinition flavor,
        IReadOnlyList<RuntimeDefinition> runtimes,
        string packageWork,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(packageWork, flavor.FacadePackageId);
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(repositoryRoot, "packaging", "README.md"), Path.Combine(root, "README.md"));
        var dependencies = new List<(string Id, string Version)> { (flavor.CorePackageId, plan.PackageVersion) };
        dependencies.AddRange(runtimes.Select(runtime => ($"{flavor.FacadePackageId}.{runtime.Rid}", plan.PackageVersion)));
        string nuspec = CreateNuspec(
            flavor.FacadePackageId,
            plan,
            "One-reference FFmpeg and FFprobe deployment for supported .NET Runtime Identifiers.",
            dependencies,
            [("README.md", string.Empty)]);
        return await PackNuspecAsync(
            root,
            nuspec,
            flavor.FacadePackageId,
            plan.PackageVersion,
            candidateDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PackNuspecAsync(
        string root,
        string nuspec,
        string packageId,
        string version,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        string nuspecPath = Path.Combine(root, packageId + ".nuspec");
        await File.WriteAllTextAsync(nuspecPath, nuspec, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        string projectPath = Path.Combine(root, "pack.csproj");
        string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <IncludeBuildOutput>false</IncludeBuildOutput>
                <NuspecFile>$(PackageNuspecFile)</NuspecFile>
                <NuspecBasePath>$(MSBuildProjectDirectory)</NuspecBasePath>
              </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(projectPath, project, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        CommandResult result = await processRunner.RunAsync(
            "dotnet",
            [
                "pack", projectPath,
                "--configuration", "Release",
                "--output", candidateDirectory,
                $"-p:PackageNuspecFile={nuspecPath}",
                "--nologo"
            ],
            root,
            PackTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "PackageCreationFailed");
        return RequireSingleCandidate(candidateDirectory, $"{packageId}.{version}.nupkg");
    }

    internal static string CreateNuspec(
        string packageId,
        ReleasePlan plan,
        string description,
        List<(string Id, string Version)> dependencies,
        List<(string Source, string Target)> files,
        string? licenseFile = null)
    {
        string dependencyXml = dependencies.Count == 0
            ? string.Empty
            : "<dependencies>" + string.Concat(dependencies.Select(dependency =>
                $"<dependency id=\"{Escape(dependency.Id)}\" version=\"[{Escape(dependency.Version)}]\" />")) + "</dependencies>";
        string fileXml = string.Concat(files.Select(file =>
            $"<file src=\"{Escape(file.Source)}\" target=\"{Escape(file.Target)}\" />"));
        string licenseXml = licenseFile is null
            ? "<license type=\"expression\">LGPL-2.1-or-later</license>"
            : $"<license type=\"file\">{Escape(licenseFile)}</license>";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Escape(packageId)}</id>
                <version>{Escape(plan.PackageVersion)}</version>
                <authors>Supprocom</authors>
                <owners>Supprocom</owners>
                <requireLicenseAcceptance>false</requireLicenseAcceptance>
                {licenseXml}
                <readme>README.md</readme>
                <projectUrl>https://github.com/Supprocom/FFmpeg.Binaries</projectUrl>
                <repository type="git" url="https://github.com/Supprocom/FFmpeg.Binaries.git" commit="{Escape(plan.ReleaseProgramCommit)}" />
                <description>{Escape(description)}</description>
                <tags>ffmpeg ffprobe native binaries multimedia</tags>
                {dependencyXml}
              </metadata>
              <files>{fileXml}</files>
            </package>
            """;
    }

    private static string ResolveAcceptedWorkerDirectory(
        string planDirectory,
        string? workerArtifactRoot,
        string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(workerArtifactRoot))
        {
            return Path.Combine(planDirectory, "workers", runtimeIdentifier, "accepted");
        }

        string root = Path.GetFullPath(workerArtifactRoot);
        string accepted = Path.GetFullPath(Path.Combine(root, runtimeIdentifier));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!(accepted + Path.DirectorySeparatorChar).StartsWith(rootPrefix, comparison))
        {
            throw new ReleaseFailureException("UnsafeWorkerArtifactPath", "A worker artifact escaped its configured root.");
        }

        return accepted;
    }

    private async Task<(WorkerManifest Manifest, string Payload)> ReadAndValidateWorkerAsync(
        ReleasePlan plan,
        string planHash,
        string planDirectory,
        string? workerArtifactRoot,
        RuntimeDefinition runtime,
        CancellationToken cancellationToken)
    {
        string accepted = ResolveAcceptedWorkerDirectory(planDirectory, workerArtifactRoot, runtime.Rid);
        string manifestPath = Path.Combine(accepted, "worker-manifest.json");
        string manifestHashPath = Path.Combine(accepted, "worker-manifest.sha256");
        string payload = Path.Combine(accepted, "payload");
        if (!File.Exists(manifestPath) || !File.Exists(manifestHashPath) || !Directory.Exists(payload))
        {
            throw new ReleaseFailureException("WorkerArtifactMissing", $"The accepted {runtime.Rid} worker artifact is missing.");
        }

        string manifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        string recordedManifestHash = (await File.ReadAllTextAsync(manifestHashPath, cancellationToken).ConfigureAwait(false))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        if (!manifestHash.Equals(recordedManifestHash, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("WorkerManifestHashMismatch", $"The {runtime.Rid} worker manifest changed after acceptance.");
        }

        WorkerManifest manifest = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            ReleaseJsonContext.Default.WorkerManifest)
            ?? throw new ReleaseFailureException("WorkerManifestInvalid", $"The {runtime.Rid} worker manifest is empty.");
        WorkerFile? toolchainFile = manifest.Files.SingleOrDefault(file => file.Path.Equals(
            "BUILD-METADATA/toolchain-provenance.json",
            StringComparison.Ordinal));
        if (manifest.SchemaVersion != 3 ||
            !manifest.PlanSha256.Equals(planHash, StringComparison.Ordinal) ||
            !manifest.Version.Equals(plan.Version, StringComparison.Ordinal) ||
            !manifest.SourceCommit.Equals(plan.SourceCommit, StringComparison.Ordinal) ||
            !manifest.ArchiveSha256.Equals(plan.ArchiveSha256, StringComparison.Ordinal) ||
            !manifest.Flavor.Equals(plan.Flavor, StringComparison.Ordinal) ||
            !manifest.RuntimeIdentifier.Equals(runtime.Rid, StringComparison.Ordinal) ||
            !manifest.Worker.Equals(runtime.Worker, StringComparison.Ordinal) ||
            !manifest.CpuBaseline.Equals(runtime.CpuBaseline, StringComparison.Ordinal) ||
            toolchainFile is null ||
            !manifest.ToolchainProvenanceSha256.Equals(toolchainFile.Sha256, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.ArchitectureEvidence) ||
            string.IsNullOrWhiteSpace(manifest.DynamicDependencyEvidence) ||
            string.IsNullOrWhiteSpace(manifest.AbiCompatibilityEvidence) ||
            string.IsNullOrWhiteSpace(manifest.HardeningEvidence) ||
            !manifest.Files.Any(file => file.Path.Equals("BUILD-METADATA/abi-compatibility.txt", StringComparison.Ordinal)) ||
            !manifest.Files.Any(file => file.Path.Equals("BUILD-METADATA/dynamic-dependencies.txt", StringComparison.Ordinal)) ||
            !manifest.Files.Any(file => file.Path.Equals("BUILD-METADATA/hardening.txt", StringComparison.Ordinal)) ||
            !manifest.Reproducible ||
            !manifest.SmokeTestPassed)
        {
            throw new ReleaseFailureException("WorkerManifestMismatch", $"The {runtime.Rid} worker manifest does not satisfy the immutable plan.");
        }

        foreach (WorkerFile expected in manifest.Files)
        {
            string path = Path.GetFullPath(Path.Combine(payload, expected.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!(path + Path.DirectorySeparatorChar).StartsWith(Path.GetFullPath(payload) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(path))
            {
                throw new ReleaseFailureException("WorkerPayloadInvalid", $"The {runtime.Rid} worker payload is missing '{expected.Path}'.");
            }

            if (!expected.Mode.Equals("windows", StringComparison.Ordinal))
            {
                if (expected.Mode is not ("0644" or "0755"))
                {
                    throw new ReleaseFailureException("WorkerPayloadModeInvalid", $"The {runtime.Rid} payload file '{expected.Path}' has an unsafe recorded mode.");
                }

                if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(workerArtifactRoot))
                {
                    File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(expected.Mode, 8));
                }
            }

            var info = new FileInfo(path);
            string hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.Length != expected.Size || !hash.Equals(expected.Sha256, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException("WorkerPayloadHashMismatch", $"The {runtime.Rid} payload file '{expected.Path}' changed after acceptance.");
            }

            if (!OperatingSystem.IsWindows() && !expected.Mode.Equals("windows", StringComparison.Ordinal))
            {
                string actualMode = Convert.ToString((int)File.GetUnixFileMode(path), 8).PadLeft(4, '0');
                if (!actualMode.Equals(expected.Mode, StringComparison.Ordinal))
                {
                    throw new ReleaseFailureException("WorkerPayloadModeMismatch", $"The {runtime.Rid} payload file '{expected.Path}' changed mode after acceptance.");
                }
            }
        }

        int actualCount = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Count();
        if (actualCount != manifest.Files.Count)
        {
            throw new ReleaseFailureException("WorkerPayloadUnexpectedFile", $"The {runtime.Rid} payload contains an unmanifested file.");
        }

        return (manifest, payload);
    }

    private static async Task<FrozenPackage> InspectAndFreezeAsync(
        string candidate,
        string expectedId,
        string expectedVersion,
        string kind,
        string frozenDirectory,
        CancellationToken cancellationToken)
    {
        ValidatePackageArchive(candidate, expectedId, expectedVersion, kind);
        var info = new FileInfo(candidate);
        if (info.Length > MaximumPackageBytes)
        {
            throw new ReleaseFailureException("PackageTooLarge", $"Package '{expectedId}' exceeds the 250 MiB release limit.");
        }

        string hash = await HashFileAsync(candidate, cancellationToken).ConfigureAwait(false);
        string destination = Path.Combine(frozenDirectory, Path.GetFileName(candidate));
        if (File.Exists(destination))
        {
            string existingHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!existingHash.Equals(hash, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException("FrozenArtifactConflict", $"Frozen package identity '{expectedId} {expectedVersion}' already has different bytes.");
            }

            File.Delete(candidate);
        }
        else
        {
            File.Move(candidate, destination, overwrite: false);
        }

        MakeReadOnly(destination);
        return new FrozenPackage(expectedId, expectedVersion, Path.GetFileName(destination), info.Length, hash, kind);
    }

    private static void ValidatePackageArchive(string path, string expectedId, string expectedVersion, string kind)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (names.Length != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new ReleaseFailureException("DuplicatePackageEntry", $"Package '{expectedId}' contains duplicate archive paths.");
        }

        if (names.Any(name => name.StartsWith('/') ||
                              name.Contains("../", StringComparison.Ordinal) ||
                              name.Contains("SUPPROCOM_NUGET_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("GITHUB_PACKAGES_TOKEN", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReleaseFailureException("UnsafePackageEntry", $"Package '{expectedId}' contains an unsafe archive path.");
        }

        ZipArchiveEntry nuspecEntry = archive.Entries.SingleOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new ReleaseFailureException("PackageMetadataMissing", $"Package '{expectedId}' has no nuspec metadata.");
        using Stream nuspecStream = nuspecEntry.Open();
        XDocument document = XDocument.Load(nuspecStream, LoadOptions.None);
        XElement metadata = document.Descendants().Single(element => element.Name.LocalName == "metadata");
        string id = metadata.Elements().Single(element => element.Name.LocalName == "id").Value;
        string version = metadata.Elements().Single(element => element.Name.LocalName == "version").Value;
        if (!id.Equals(expectedId, StringComparison.Ordinal) || !version.Equals(expectedVersion, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("PackageIdentityMismatch", $"Package '{expectedId}' metadata has an unexpected identity.");
        }

        XElement? repository = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "repository");
        XElement? license = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "license");
        bool validLicense = kind.Equals("source", StringComparison.Ordinal)
            ? license is not null &&
              license.Attribute("type")?.Value == "file" &&
              license.Value == "licenses/FFmpeg-LICENSE.md" &&
              RequiredSourceLicenseEntries.All(name => names.Contains(name, StringComparer.Ordinal))
            : license is not null &&
              license.Attribute("type")?.Value == "expression" &&
              license.Value == "LGPL-2.1-or-later";
        if (repository?.Attribute("url")?.Value != "https://github.com/Supprocom/FFmpeg.Binaries.git" ||
            !validLicense ||
            !names.Contains("README.md", StringComparer.Ordinal))
        {
            throw new ReleaseFailureException("PackageMetadataInvalid", $"Package '{expectedId}' lacks approved repository, license, or README metadata.");
        }

        foreach (XElement dependency in metadata.Descendants().Where(element => element.Name.LocalName == "dependency"))
        {
            if (dependency.Attribute("version")?.Value != $"[{expectedVersion}]")
            {
                throw new ReleaseFailureException("PackageDependencyNotExact", $"Package '{expectedId}' has a non-exact family dependency.");
            }
        }

        if (kind == "runtime")
        {
            const string runtimePackagePrefix = "Supprocom.FFmpeg.Binaries.";
            if (!expectedId.StartsWith(runtimePackagePrefix, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException("RuntimePackageIdentityInvalid", $"Runtime package '{expectedId}' has an unexpected identity prefix.");
            }

            string rid = expectedId[runtimePackagePrefix.Length..];
            string prefix = $"runtimes/{rid}/native/ffmpeg/";
            if (!names.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)) ||
                !names.Any(name => name.StartsWith("buildTransitive/", StringComparison.Ordinal)))
            {
                throw new ReleaseFailureException("RuntimePackageLayoutInvalid", $"Runtime package '{expectedId}' lacks its native or transitive-build assets.");
            }
        }
    }

    private static string RequireSingleCandidate(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new ReleaseFailureException("PackageCandidateMissing", $"Expected package candidate '{fileName}' was not created.");
        }

        return path;
    }

    private static void MakeReadOnly(string path)
    {
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

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
            throw new ReleaseFailureException("UnsafePackagePath", "A package-work directory escaped its plan-owned root.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }

        Directory.CreateDirectory(fullPath);
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
            throw new ReleaseFailureException(code, $"A required package-assembly command failed.\n{diagnostic}");
        }
    }
}
