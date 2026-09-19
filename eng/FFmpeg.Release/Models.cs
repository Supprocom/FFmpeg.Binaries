using System.Text.Json.Serialization;

namespace Supprocom.FFmpeg.Release;

internal sealed record ReleaseMatrix(
    int SchemaVersion,
    RepositoryDefinition Repository,
    IReadOnlyList<FeedDefinition> Feeds,
    IReadOnlyList<VersionDefinition> Versions,
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

internal sealed record RuntimeDefinition(string Rid, string Os, string Architecture, string Worker);

internal sealed record FlavorDefinition(
    string Name,
    string FacadePackageId,
    string CorePackageId,
    string SourcePackageId,
    IReadOnlyList<string> ConfigureArguments);

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
    string Version,
    string SourceCommit,
    string ArchiveSha256,
    string Flavor,
    string RuntimeIdentifier,
    string Worker,
    string ConfigureArgumentsSha256,
    IReadOnlyList<string> ConfigureArguments,
    IReadOnlyList<string> FateTests,
    string ArchitectureEvidence,
    string DynamicDependencyEvidence,
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
    string Version,
    bool CompleteRuntimeMatrix,
    IReadOnlyList<FrozenPackage> Packages,
    DateTimeOffset FrozenUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReleaseMatrix))]
[JsonSerializable(typeof(ReleasePlan))]
[JsonSerializable(typeof(PersistedPlan))]
[JsonSerializable(typeof(SourceVerificationResult))]
[JsonSerializable(typeof(WorkerManifest))]
[JsonSerializable(typeof(List<WorkerFile>))]
[JsonSerializable(typeof(FrozenReleaseManifest))]
[JsonSerializable(typeof(List<FrozenPackage>))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
