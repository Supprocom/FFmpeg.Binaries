using System.Text.Json.Serialization;

namespace Supprocom.FFmpeg.Release;

internal sealed record ReleaseMatrix(
    int SchemaVersion,
    RepositoryDefinition Repository,
    IReadOnlyList<FeedDefinition> Feeds,
    IReadOnlyList<VersionDefinition> Versions,
    IReadOnlyList<ReleaseDefinition> Releases,
    IReadOnlyList<RuntimeDefinition> RuntimeIdentifiers,
    IReadOnlyList<FlavorDefinition> Flavors);

internal sealed record RepositoryDefinition(string Origin, string OfficialSource);

internal sealed record FeedDefinition(string Name, string ServiceIndex);

internal sealed record VersionDefinition(
    string Version,
    string Tag,
    string Status,
    string ReleaseDate,
    long? SourceDateEpoch = null,
    string? ArchiveUri = null,
    string? SignatureUri = null,
    string? ReleaseKeyUri = null,
    string? ReleaseKeyFingerprint = null,
    string? ArchiveSha256 = null,
    string? TagObject = null,
    string? SourceCommit = null);

internal sealed record ReleaseDefinition(
    string PackageVersion,
    string SourceVersion,
    string Flavor,
    string Status,
    bool Default = false);

internal sealed record RuntimeDefinition(
    string Rid,
    string Os,
    string Architecture,
    string Worker,
    string CpuBaseline,
    ToolchainDefinition Toolchain,
    string? MinimumOsVersion = null,
    string? Libc = null,
    string? MinimumLibcVersion = null,
    string? WorkerImage = null,
    IReadOnlyList<string>? SystemDependencies = null);

internal sealed record ToolchainDefinition(
    string EnvironmentIdentity,
    string RepositorySnapshot,
    string PackageManager,
    IReadOnlyDictionary<string, string> Packages,
    IReadOnlyDictionary<string, string>? MediaPackages = null,
    IReadOnlyDictionary<string, string>? FullPackages = null,
    string? PackageLockSha256 = null);

internal sealed record ToolchainProvenance(
    int SchemaVersion,
    string RuntimeIdentifier,
    string EnvironmentIdentity,
    string RepositorySnapshot,
    string PackageManager,
    IReadOnlyDictionary<string, string> ApprovedPackages,
    IReadOnlyList<string> InstalledPackages,
    IReadOnlyList<string> RepositoryEvidence,
    IReadOnlyList<string> ToolEvidence,
    string? PackageLockSha256 = null);

internal sealed record BundledComponent(
    string FileName,
    string PackageName,
    string PackageVersion,
    string PackageManager,
    string LicensePath);

internal sealed record FlavorDefinition(
    string Name,
    string FacadePackageId,
    string CorePackageId,
    string SourcePackageId,
    string LicenseExpression,
    bool IncludeFfplay,
    IReadOnlyList<string> ConfigureArguments,
    IReadOnlyList<string> RequiredEncoders,
    IReadOnlyList<string> RequiredDecoders,
    IReadOnlyList<string> RequiredFilters,
    IReadOnlyList<string> RequiredProtocols);

internal sealed record ReleasePlan(
    int SchemaVersion,
    string RepositoryOrigin,
    string ReleaseProgramCommit,
    string MatrixSha256,
    string Version,
    string PackageVersion,
    string Tag,
    string TagObject,
    string SourceCommit,
    string ArchiveSha256,
    string Flavor,
    IReadOnlyList<string> RuntimeIdentifiers,
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<string> Feeds,
    bool ReducedValidation);

internal sealed record PersistedPlan(
    string PlanSha256,
    DateTimeOffset CreatedUtc,
    ReleasePlan Plan);

internal sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

internal sealed record SourceVerificationResult(
    string ArchivePath,
    string SignaturePath,
    string ReleaseKeyPath,
    string ArchiveSha256,
    string SignatureFingerprint,
    string SourceCommit,
    string TagObject);

internal sealed record WorkerFile(
    string Path,
    long Size,
    string Sha256,
    string Mode);

internal sealed record WorkerManifest(
    int SchemaVersion,
    string PlanSha256,
    string SourceVersion,
    string PackageVersion,
    string SourceCommit,
    string ArchiveSha256,
    string Flavor,
    string RuntimeIdentifier,
    string Worker,
    string CpuBaseline,
    string ToolchainProvenanceSha256,
    string ConfigureArgumentsSha256,
    IReadOnlyList<string> ConfigureArguments,
    IReadOnlyList<string> FateTests,
    string ArchitectureEvidence,
    string DynamicDependencyEvidence,
    string AbiCompatibilityEvidence,
    string HardeningEvidence,
    bool SmokeTestPassed,
    bool Reproducible,
    IReadOnlyList<WorkerFile> Files,
    DateTimeOffset CompletedUtc);

internal sealed record FrozenPackage(
    string Id,
    string Version,
    string FileName,
    long Size,
    string Sha256,
    string Kind);

internal sealed record FrozenReleaseManifest(
    int SchemaVersion,
    string PlanSha256,
    string SourceVersion,
    string PackageVersion,
    string Flavor,
    string ReleaseProgramCommit,
    string MatrixSha256,
    bool CompleteRuntimeMatrix,
    string SbomFileName,
    string SbomSha256,
    IReadOnlyList<FrozenPackage> Packages,
    DateTimeOffset FrozenUtc);

internal sealed record ConsumerAttestation(
    int SchemaVersion,
    string PlanSha256,
    string ReleaseManifestSha256,
    string SourceVersion,
    string PackageVersion,
    string Flavor,
    string RuntimeIdentifier,
    IReadOnlyList<string> Scenarios,
    DateTimeOffset CompletedUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReleaseMatrix))]
[JsonSerializable(typeof(ReleasePlan))]
[JsonSerializable(typeof(PersistedPlan))]
[JsonSerializable(typeof(SourceVerificationResult))]
[JsonSerializable(typeof(ToolchainProvenance))]
[JsonSerializable(typeof(List<BundledComponent>))]
[JsonSerializable(typeof(WorkerManifest))]
[JsonSerializable(typeof(List<WorkerFile>))]
[JsonSerializable(typeof(FrozenReleaseManifest))]
[JsonSerializable(typeof(List<FrozenPackage>))]
[JsonSerializable(typeof(ConsumerAttestation))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
