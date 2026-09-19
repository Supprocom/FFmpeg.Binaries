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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReleaseMatrix))]
[JsonSerializable(typeof(ReleasePlan))]
[JsonSerializable(typeof(PersistedPlan))]
[JsonSerializable(typeof(SourceVerificationResult))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
