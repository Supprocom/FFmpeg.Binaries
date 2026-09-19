using System.Text.Json;

namespace Supprocom.FFmpeg.Release;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            string repositoryRoot = FindRepositoryRoot();
            if (options.SelfTest)
            {
                SelfTests.Run(repositoryRoot);
                return 0;
            }

            string matrixPath = Path.Combine(repositoryRoot, "eng", "release-matrix.json");
            (ReleaseMatrix matrix, string matrixHash) = MatrixLoader.Load(matrixPath);
            if (options.EmitWorkerMatrix)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                    new
                    {
                        include = matrix.RuntimeIdentifiers.Select(runtime => new
                        {
                            runtimeIdentifier = runtime.Rid,
                            os = runtime.Os,
                            architecture = runtime.Architecture,
                            runner = runtime.Worker
                        })
                    })).ConfigureAwait(false);
                return 0;
            }

            VersionDefinition version = SelectVersion(matrix, options.Version);
            FlavorDefinition flavor = matrix.Flavors.Single(item => item.Name.Equals("lgpl", StringComparison.Ordinal));
            var runner = new ProcessRunner();
            string releaseCommit = await new RepositoryGate(runner).VerifyAsync(
                repositoryRoot,
                matrix.Repository.Origin,
                options.AllowDirty,
                CancellationToken.None).ConfigureAwait(false);
            ReleasePlan plan = CreatePlan(matrix, matrixHash, version, flavor, releaseCommit, options.AllowDirty);
            string releaseRoot = ResolveReleaseRoot(options.ReleaseRoot, repositoryRoot);
            (string planHash, string planPath) = PlanWriter.Write(releaseRoot, plan);
            string planDirectory = Path.GetDirectoryName(planPath)!;

            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Supprocom.FFmpeg.Release/1.0");
            SourceVerificationResult source = await new SourceVerifier(httpClient, runner).VerifyAsync(
                version,
                matrix.Repository.OfficialSource,
                planDirectory,
                CancellationToken.None).ConfigureAwait(false);
            if (options.Worker)
            {
                if (string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
                {
                    throw new ReleaseFailureException("RuntimeIdentifierRequired", "Worker mode requires --rid <Runtime Identifier>.");
                }

                RuntimeDefinition runtime = matrix.RuntimeIdentifiers.SingleOrDefault(
                    item => item.Rid.Equals(options.RuntimeIdentifier, StringComparison.Ordinal))
                    ?? throw new ReleaseFailureException(
                        "UnsupportedRuntimeIdentifier",
                        $"Runtime Identifier '{options.RuntimeIdentifier}' is not in the approved matrix.");
                string manifestPath = await new NativeWorker(runner).BuildAsync(
                    repositoryRoot,
                    plan,
                    planHash,
                    planDirectory,
                    version,
                    flavor,
                    runtime,
                    source,
                    CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Accepted worker manifest: {manifestPath}");
                return 0;
            }

            if (options.Assemble)
            {
                IReadOnlyList<RuntimeDefinition> packageRuntimes;
                if (options.AllowPartial)
                {
                    if (string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
                    {
                        throw new ReleaseFailureException("RuntimeIdentifierRequired", "Partial package assembly requires --rid <Runtime Identifier>.");
                    }

                    packageRuntimes =
                    [
                        matrix.RuntimeIdentifiers.SingleOrDefault(
                            item => item.Rid.Equals(options.RuntimeIdentifier, StringComparison.Ordinal))
                        ?? throw new ReleaseFailureException(
                            "UnsupportedRuntimeIdentifier",
                            $"Runtime Identifier '{options.RuntimeIdentifier}' is not in the approved matrix.")
                    ];
                }
                else
                {
                    packageRuntimes = matrix.RuntimeIdentifiers;
                }

                string manifestPath = await new PackageAssembler(runner).AssembleAsync(
                    repositoryRoot,
                    plan,
                    planHash,
                    planDirectory,
                    flavor,
                    source,
                    packageRuntimes,
                    completeRuntimeMatrix: !options.AllowPartial,
                    CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Frozen release manifest: {manifestPath}");
                return 0;
            }

            CredentialAvailability credentials = CredentialProbe.InspectPresence();

            Console.WriteLine($"Plan: {planHash}");
            Console.WriteLine($"Plan file: {planPath}");
            Console.WriteLine($"FFmpeg source: {source.ArchiveSha256}");
            Console.WriteLine($"Source commit: {source.SourceCommit}");
            Console.WriteLine($"Required runtimes: {plan.RuntimeIdentifiers.Count}");
            Console.WriteLine($"Publication credentials: {(credentials.CanPublishBoth ? "both feeds available" : "staging only")}");
            Console.WriteLine("Source validation and immutable release planning completed.");
            return 0;
        }
        catch (ReleaseFailureException exception)
        {
            await Console.Error.WriteLineAsync($"error {exception.Code}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"error UnexpectedFailure: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static ReleasePlan CreatePlan(
        ReleaseMatrix matrix,
        string matrixHash,
        VersionDefinition version,
        FlavorDefinition flavor,
        string releaseCommit,
        bool reducedValidation)
    {
        var packageIds = new List<string>
        {
            flavor.SourcePackageId,
            flavor.CorePackageId
        };
        packageIds.AddRange(matrix.RuntimeIdentifiers.Select(item => $"{flavor.FacadePackageId}.{item.Rid}"));
        packageIds.Add(flavor.FacadePackageId);
        return new ReleasePlan(
            1,
            matrix.Repository.Origin,
            releaseCommit,
            matrixHash,
            version.Version,
            version.Version,
            version.Tag,
            version.TagObject!,
            version.SourceCommit!,
            version.ArchiveSha256!,
            flavor.Name,
            matrix.RuntimeIdentifiers.Select(item => item.Rid).ToArray(),
            packageIds,
            matrix.Feeds.Select(item => item.ServiceIndex).ToArray(),
            reducedValidation);
    }

    private static VersionDefinition SelectVersion(ReleaseMatrix matrix, string? requestedVersion)
    {
        VersionDefinition[] approved = matrix.Versions
            .Where(item => item.Status.Equals("approved", StringComparison.Ordinal))
            .ToArray();
        if (requestedVersion is null)
        {
            return approved
                .OrderByDescending(item => Version.Parse(item.Version))
                .First();
        }

        return approved.SingleOrDefault(item => item.Version.Equals(requestedVersion, StringComparison.Ordinal))
            ?? throw new ReleaseFailureException("VersionNotApproved", $"FFmpeg version '{requestedVersion}' is not approved for release.");
    }

    private static string ResolveReleaseRoot(string? configuredPath, string repositoryRoot)
    {
        string path = configuredPath ?? Environment.GetEnvironmentVariable("SUPPROCOM_FFMPEG_RELEASE_ROOT") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Supprocom",
                "FFmpeg.Binaries",
                "releases");
        string fullPath = Path.GetFullPath(path);
        string fullRepository = Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar;
        if ((fullPath + Path.DirectorySeparatorChar).StartsWith(fullRepository, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("ReleaseRootInsideRepository", "Release artifacts must be staged outside the source checkout.");
        }

        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string FindRepositoryRoot()
    {
        string current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "FFmpeg.Release.csproj")) && Directory.Exists(Path.Combine(current, ".git")))
        {
            return current;
        }

        throw new ReleaseFailureException("RepositoryRootNotFound", "Run the release program from the repository root.");
    }

    private sealed record Options(
        bool SelfTest,
        bool AllowDirty,
        bool EmitWorkerMatrix,
        bool Worker,
        bool Assemble,
        bool AllowPartial,
        string? RuntimeIdentifier,
        string? Version,
        string? ReleaseRoot)
    {
        public static Options Parse(string[] args)
        {
            bool selfTest = false;
            bool allowDirty = false;
            bool emitWorkerMatrix = false;
            bool worker = false;
            bool assemble = false;
            bool allowPartial = false;
            string? runtimeIdentifier = null;
            string? version = null;
            string? releaseRoot = null;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--self-test":
                        selfTest = true;
                        break;
                    case "--allow-dirty":
                        allowDirty = true;
                        break;
                    case "--emit-worker-matrix":
                        emitWorkerMatrix = true;
                        break;
                    case "--worker":
                        worker = true;
                        break;
                    case "--assemble":
                        assemble = true;
                        break;
                    case "--allow-partial":
                        allowPartial = true;
                        break;
                    case "--rid" when index + 1 < args.Length:
                        runtimeIdentifier = args[++index];
                        break;
                    case "--version" when index + 1 < args.Length:
                        version = args[++index];
                        break;
                    case "--release-root" when index + 1 < args.Length:
                        releaseRoot = args[++index];
                        break;
                    default:
                        throw new ReleaseFailureException("InvalidArguments", $"Unknown or incomplete argument '{args[index]}'.");
                }
            }

            return new Options(
                selfTest,
                allowDirty,
                emitWorkerMatrix,
                worker,
                assemble,
                allowPartial,
                runtimeIdentifier,
                version,
                releaseRoot);
        }
    }
}
