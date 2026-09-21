using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Supprocom.FFmpeg.Release;

internal sealed class PackagePublisher(HttpClient httpClient)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(12);
    private const long MaximumDownloadedPackageBytes = 250L * 1024 * 1024;

    public async Task PublishAsync(
        ReleaseMatrix matrix,
        string matrixHash,
        string releaseCommit,
        VersionDefinition version,
        ReleaseDefinition releaseDefinition,
        FlavorDefinition flavor,
        string packageRoot,
        string attestationRoot,
        CancellationToken cancellationToken)
    {
        string packages = Path.GetFullPath(packageRoot);
        FrozenReleaseManifest release = await ValidateFrozenSetAsync(
            matrix,
            matrixHash,
            releaseCommit,
            version,
            releaseDefinition,
            flavor,
            packages,
            cancellationToken).ConfigureAwait(false);
        await ValidateConsumerAttestationsAsync(
            matrix,
            release,
            packages,
            Path.GetFullPath(attestationRoot),
            cancellationToken).ConfigureAwait(false);

        PublicationCredentials credentials = PublicationCredentials.LoadRequired();
        IReadOnlyList<PublicationFeed> feeds = await CreateFeedsAsync(
            matrix,
            credentials,
            cancellationToken).ConfigureAwait(false);
        string journal = Path.Combine(packages, "publication-journal.jsonl");
        var states = new Dictionary<(string Feed, string Package), RemotePackageState>();

        foreach (PublicationFeed feed in feeds)
        {
            foreach (FrozenPackage package in release.Packages)
            {
                string path = Path.Combine(packages, package.FileName);
                RemotePackageState state = await InspectRemotePackageAsync(
                    feed,
                    package,
                    path,
                    cancellationToken).ConfigureAwait(false);
                if (state == RemotePackageState.Conflict)
                {
                    throw new ReleaseFailureException(
                        "ExistingPackageConflict",
                        $"{feed.Name} already contains different bytes for {package.Id} {package.Version}.");
                }

                states[(feed.Name, package.Id)] = state;
                await AppendJournalAsync(
                    journal,
                    new
                    {
                        type = "preflight",
                        feed = feed.Name,
                        packageId = package.Id,
                        package.Version,
                        status = state.ToString(),
                        utc = DateTimeOffset.UtcNow
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        FrozenPackage[] ordered = release.Packages
            .OrderBy(package => PackageOrder(package.Kind))
            .ThenBy(package => package.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (FrozenPackage package in ordered)
        {
            if (package.Kind.Equals("facade", StringComparison.Ordinal))
            {
                await VerifyDependenciesAvailableAsync(
                    feeds,
                    release.Packages.Where(candidate => !candidate.Kind.Equals("facade", StringComparison.Ordinal)),
                    packages,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (PublicationFeed feed in feeds)
            {
                if (states[(feed.Name, package.Id)] == RemotePackageState.Identical)
                {
                    continue;
                }

                string path = Path.Combine(packages, package.FileName);
                await AppendJournalAsync(
                    journal,
                    new
                    {
                        type = "upload-started",
                        feed = feed.Name,
                        packageId = package.Id,
                        package.Version,
                        package.Sha256,
                        utc = DateTimeOffset.UtcNow
                    },
                    cancellationToken).ConfigureAwait(false);
                bool requestAccepted = false;
                try
                {
                    await UploadAsync(feed, package, path, cancellationToken).ConfigureAwait(false);
                    requestAccepted = true;
                }
                catch (Exception exception) when (IsUncertainUploadFailure(exception))
                {
                    await AppendJournalAsync(
                        journal,
                        new
                        {
                            type = "upload-uncertain",
                            feed = feed.Name,
                            packageId = package.Id,
                            package.Version,
                            error = exception.GetType().Name,
                            utc = DateTimeOffset.UtcNow
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }

                RemotePackageState visible = await WaitForRemotePackageAsync(
                    feed,
                    package,
                    path,
                    cancellationToken).ConfigureAwait(false);
                if (visible != RemotePackageState.Identical)
                {
                    string code = requestAccepted ? "PublishedPackageNotVisible" : "PackageUploadUncertain";
                    throw new ReleaseFailureException(
                        code,
                        $"{feed.Name} did not expose the accepted frozen bytes for {package.Id} {package.Version}.");
                }

                states[(feed.Name, package.Id)] = RemotePackageState.Identical;
                await AppendJournalAsync(
                    journal,
                    new
                    {
                        type = "upload-accepted",
                        feed = feed.Name,
                        packageId = package.Id,
                        package.Version,
                        package.Sha256,
                        utc = DateTimeOffset.UtcNow
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await VerifyDependenciesAvailableAsync(feeds, release.Packages, packages, cancellationToken)
            .ConfigureAwait(false);
        await AppendJournalAsync(
            journal,
            new
            {
                type = "publication-complete",
                release.PlanSha256,
                release.PackageVersion,
                feeds = feeds.Select(feed => feed.Name).ToArray(),
                packageCount = release.Packages.Count,
                utc = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Published and verified {release.Packages.Count} frozen packages on {feeds.Count} feeds.");
    }

    private async Task<IReadOnlyList<PublicationFeed>> CreateFeedsAsync(
        ReleaseMatrix matrix,
        PublicationCredentials credentials,
        CancellationToken cancellationToken)
    {
        FeedDefinition nugetDefinition = matrix.Feeds.Single(feed => feed.Name.Equals("nuget.org", StringComparison.Ordinal));
        FeedDefinition githubDefinition = matrix.Feeds.Single(feed => feed.Name.Equals("github", StringComparison.Ordinal));
        var feeds = new[]
        {
            new PublicationFeed(
                githubDefinition.Name,
                new Uri(githubDefinition.ServiceIndex, UriKind.Absolute),
                credentials.GitHubToken,
                credentials.GitHubUser,
                useBasicAuthentication: true),
            new PublicationFeed(
                nugetDefinition.Name,
                new Uri(nugetDefinition.ServiceIndex, UriKind.Absolute),
                credentials.NuGetApiKey,
                userName: null,
                useBasicAuthentication: false)
        };
        foreach (PublicationFeed feed in feeds)
        {
            await DiscoverEndpointsAsync(feed, cancellationToken).ConfigureAwait(false);
        }

        return feeds;
    }

    private async Task DiscoverEndpointsAsync(PublicationFeed feed, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, feed.ServiceIndex, feed, includeApiKey: false);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReleaseFailureException(
                "FeedAuthenticationFailed",
                $"{feed.Name} service discovery returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement[] resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        feed.PackageBaseAddress = FindResource(resources, "PackageBaseAddress/");
        feed.PackagePublish = FindResource(resources, "PackagePublish/");
    }

    private static Uri FindResource(IEnumerable<JsonElement> resources, string typePrefix)
    {
        foreach (JsonElement resource in resources)
        {
            if (!resource.TryGetProperty("@type", out JsonElement type))
            {
                continue;
            }

            IEnumerable<string> types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                : [type.GetString() ?? string.Empty];
            if (types.Any(value => value.StartsWith(typePrefix, StringComparison.Ordinal)))
            {
                return new Uri(resource.GetProperty("@id").GetString()!, UriKind.Absolute);
            }
        }

        throw new ReleaseFailureException("FeedResourceMissing", $"A feed lacks its required {typePrefix} resource.");
    }

    private async Task<RemotePackageState> InspectRemotePackageAsync(
        PublicationFeed feed,
        FrozenPackage package,
        string localPath,
        CancellationToken cancellationToken)
    {
        string id = ToNuGetPathSegment(package.Id);
        string version = ToNuGetPathSegment(package.Version);
        Uri address = new(
            EnsureTrailingSlash(feed.PackageBaseAddress!),
            $"{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(id)}.{Uri.EscapeDataString(version)}.nupkg");
        using var request = CreateRequest(HttpMethod.Get, address, feed, includeApiKey: false);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return RemotePackageState.Missing;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ReleaseFailureException(
                "FeedReadFailed",
                $"{feed.Name} returned HTTP {(int)response.StatusCode} while checking {package.Id} {package.Version}.");
        }

        byte[] remoteBytes = await ReadBoundedPackageAsync(response, cancellationToken).ConfigureAwait(false);
        string remoteIdentity = ComputePackageContentIdentity(remoteBytes);
        string localIdentity = ComputePackageContentIdentity(await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false));
        return remoteIdentity.Equals(localIdentity, StringComparison.Ordinal)
            ? RemotePackageState.Identical
            : RemotePackageState.Conflict;
    }

    private async Task UploadAsync(
        PublicationFeed feed,
        FrozenPackage package,
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "package", package.FileName);
        using var request = CreateRequest(HttpMethod.Put, feed.PackagePublish!, feed, includeApiKey: true);
        request.Headers.TryAddWithoutValidation("X-NuGet-Protocol-Version", "4.1.0");
        request.Content = content;
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or
                HttpStatusCode.TooManyRequests || status >= 500)
            {
                throw new ReleaseFailureException(
                    "PackageUploadUncertain",
                    $"{feed.Name} returned HTTP {status} while accepting {package.Id} {package.Version}.");
            }

            throw new ReleaseFailureException(
                "PackageUploadRejected",
                $"{feed.Name} rejected {package.Id} {package.Version} with HTTP {status} ({response.ReasonPhrase}).");
        }
    }

    private async Task<RemotePackageState> WaitForRemotePackageAsync(
        PublicationFeed feed,
        FrozenPackage package,
        string localPath,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + IndexTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            RemotePackageState state = await InspectRemotePackageAsync(
                feed,
                package,
                localPath,
                cancellationToken).ConfigureAwait(false);
            if (state != RemotePackageState.Missing)
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }

        return RemotePackageState.Missing;
    }

    private async Task VerifyDependenciesAvailableAsync(
        IReadOnlyList<PublicationFeed> feeds,
        IEnumerable<FrozenPackage> packages,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        foreach (PublicationFeed feed in feeds)
        {
            foreach (FrozenPackage package in packages)
            {
                RemotePackageState state = await InspectRemotePackageAsync(
                    feed,
                    package,
                    Path.Combine(packageRoot, package.FileName),
                    cancellationToken).ConfigureAwait(false);
                if (state != RemotePackageState.Identical)
                {
                    throw new ReleaseFailureException(
                        "PublishedDependencyUnavailable",
                        $"{feed.Name} does not expose the frozen {package.Id} {package.Version} package.");
                }
            }
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        PublicationFeed feed,
        bool includeApiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Supprocom.FFmpeg.Release/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (feed.UseBasicAuthentication)
        {
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{feed.UserName}:{feed.ApiKey}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        if (includeApiKey)
        {
            request.Headers.TryAddWithoutValidation("X-NuGet-ApiKey", feed.ApiKey);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseFailureException("FeedRequestTimeout", "A package-feed request exceeded its five-minute limit.");
        }
    }

    private static async Task<byte[]> ReadBoundedPackageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumDownloadedPackageBytes)
        {
            throw new ReleaseFailureException("RemotePackageTooLarge", "A remote package exceeds the 250 MiB validation limit.");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[131072];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumDownloadedPackageBytes)
            {
                throw new ReleaseFailureException("RemotePackageTooLarge", "A remote package exceeds the 250 MiB validation limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    internal static string ComputePackageContentIdentity(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        using IncrementalHash identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ZipArchiveEntry entry in archive.Entries
                     .Where(item => !item.FullName.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.FullName);
            identity.AppendData(BitConverter.GetBytes(name.Length));
            identity.AppendData(name);
            identity.AppendData(BitConverter.GetBytes(entry.Length));
            using Stream entryStream = entry.Open();
            identity.AppendData(SHA256.HashData(entryStream));
        }

        return Convert.ToHexStringLower(identity.GetHashAndReset());
    }

    private static async Task<FrozenReleaseManifest> ValidateFrozenSetAsync(
        ReleaseMatrix matrix,
        string matrixHash,
        string releaseCommit,
        VersionDefinition version,
        ReleaseDefinition releaseDefinition,
        FlavorDefinition flavor,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(packageRoot, "release-manifest.json");
        if (!Directory.Exists(packageRoot) || !File.Exists(manifestPath))
        {
            throw new ReleaseFailureException("FrozenPackageSetMissing", "The complete frozen package set is missing.");
        }

        FrozenReleaseManifest release = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            ReleaseJsonContext.Default.FrozenReleaseManifest)
            ?? throw new ReleaseFailureException("FrozenManifestInvalid", "The frozen release manifest is empty.");
        if (!release.CompleteRuntimeMatrix ||
            !release.SourceVersion.Equals(version.Version, StringComparison.Ordinal) ||
            !release.PackageVersion.Equals(releaseDefinition.PackageVersion, StringComparison.Ordinal) ||
            !release.Flavor.Equals(flavor.Name, StringComparison.Ordinal) ||
            !release.MatrixSha256.Equals(matrixHash, StringComparison.Ordinal) ||
            !release.ReleaseProgramCommit.Equals(releaseCommit, StringComparison.Ordinal) ||
            release.Packages.Count != matrix.RuntimeIdentifiers.Count + 3)
        {
            throw new ReleaseFailureException("FrozenPackageSetIncomplete", "Publication requires the complete exact-version package family.");
        }

        string sbomPath = Path.Combine(packageRoot, release.SbomFileName);
        if (!Path.GetFileName(release.SbomFileName).Equals(release.SbomFileName, StringComparison.Ordinal) ||
            !File.Exists(sbomPath) ||
            !(await HashFileAsync(sbomPath, cancellationToken).ConfigureAwait(false))
                .Equals(release.SbomSha256, StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("ReleaseSbomInvalid", "The frozen release SBOM is missing or changed.");
        }

        foreach (FrozenPackage package in release.Packages)
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

        return release;
    }

    private static async Task ValidateConsumerAttestationsAsync(
        ReleaseMatrix matrix,
        FrozenReleaseManifest release,
        string packageRoot,
        string attestationRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(attestationRoot))
        {
            throw new ReleaseFailureException("ConsumerAttestationsMissing", "Publication requires every target-native consumer attestation.");
        }

        string releaseManifestHash = await HashFileAsync(
            Path.Combine(packageRoot, "release-manifest.json"),
            cancellationToken).ConfigureAwait(false);
        foreach (RuntimeDefinition runtime in matrix.RuntimeIdentifiers)
        {
            string path = Path.Combine(attestationRoot, runtime.Rid + ".json");
            if (!File.Exists(path))
            {
                throw new ReleaseFailureException("ConsumerAttestationMissing", $"The {runtime.Rid} consumer attestation is missing.");
            }

            ConsumerAttestation attestation = JsonSerializer.Deserialize(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                ReleaseJsonContext.Default.ConsumerAttestation)
                ?? throw new ReleaseFailureException("ConsumerAttestationInvalid", $"The {runtime.Rid} consumer attestation is empty.");
            string[] required = runtime.Rid switch
            {
                "linux-x64" =>
                [
                    "runtime-self-contained-net10.0",
                    "facade-framework-dependent-net10.0",
                    "ffmpegcore-5.4.0",
                    "xabe-ffmpeg-6.0.2",
                    "facade-self-contained-net8.0",
                    "facade-trimmed-net10.0",
                    "facade-single-file-net10.0"
                ],
                "win-x64" =>
                [
                    "runtime-self-contained-net10.0",
                    "facade-framework-dependent-net10.0",
                    "ffmpegcore-5.4.0",
                    "xabe-ffmpeg-6.0.2",
                    "sdk-style-net48-build"
                ],
                _ =>
                [
                    "runtime-self-contained-net10.0",
                    "facade-framework-dependent-net10.0",
                    "ffmpegcore-5.4.0",
                    "xabe-ffmpeg-6.0.2"
                ]
            };
            if (!attestation.PlanSha256.Equals(release.PlanSha256, StringComparison.Ordinal) ||
                !attestation.ReleaseManifestSha256.Equals(releaseManifestHash, StringComparison.Ordinal) ||
                !attestation.SourceVersion.Equals(release.SourceVersion, StringComparison.Ordinal) ||
                !attestation.PackageVersion.Equals(release.PackageVersion, StringComparison.Ordinal) ||
                !attestation.Flavor.Equals(release.Flavor, StringComparison.Ordinal) ||
                !attestation.RuntimeIdentifier.Equals(runtime.Rid, StringComparison.Ordinal) ||
                required.Except(attestation.Scenarios, StringComparer.Ordinal).Any())
            {
                throw new ReleaseFailureException("ConsumerAttestationMismatch", $"The {runtime.Rid} consumer attestation does not match the frozen release.");
            }
        }
    }

    private static async Task AppendJournalAsync(
        string path,
        object entry,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry);
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Flush(true) is the only API that requests an fsync before the next irreversible upload.
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
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

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/')
            ? uri
            : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);

    private static string ToNuGetPathSegment(string value)
    {
#pragma warning disable CA1308 // The NuGet flat-container protocol requires lowercase ID and version URL segments.
        return value.ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static int PackageOrder(string kind) => kind switch
    {
        "source" => 0,
        "core" => 1,
        "runtime" => 2,
        "facade" => 3,
        _ => throw new ReleaseFailureException("UnknownPackageKind", $"Package kind '{kind}' is not publishable.")
    };

    private static bool IsUncertainUploadFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException ||
        exception is ReleaseFailureException failure &&
        failure.Code is "FeedRequestTimeout" or "PackageUploadUncertain";

    private enum RemotePackageState
    {
        Missing,
        Identical,
        Conflict
    }

    private sealed class PublicationFeed(
        string name,
        Uri serviceIndex,
        string apiKey,
        string? userName,
        bool useBasicAuthentication)
    {
        public string Name { get; } = name;
        public Uri ServiceIndex { get; } = serviceIndex;
        public string ApiKey { get; } = apiKey;
        public string? UserName { get; } = userName;
        public bool UseBasicAuthentication { get; } = useBasicAuthentication;
        public Uri? PackageBaseAddress { get; set; }
        public Uri? PackagePublish { get; set; }
    }

    private sealed class PublicationCredentials
    {
        private PublicationCredentials(string nuGetApiKey, string gitHubToken, string gitHubUser)
        {
            NuGetApiKey = nuGetApiKey;
            GitHubToken = gitHubToken;
            GitHubUser = gitHubUser;
        }

        public string NuGetApiKey { get; }
        public string GitHubToken { get; }
        public string GitHubUser { get; }

        public override string ToString() => "PublicationCredentials(redacted)";

        public static PublicationCredentials LoadRequired()
        {
            string nuget = ReadSecret("SUPPROCOM_NUGET_API_KEY", "SUPPROCOM_NUGET_API_KEY_FILE");
            string github = ReadSecret("SUPPROCOM_GITHUB_PACKAGES_TOKEN", "SUPPROCOM_GITHUB_PACKAGES_TOKEN_FILE");
            if (github.Length == 0 &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                github = Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim() ?? string.Empty;
            }

            string user = Environment.GetEnvironmentVariable("SUPPROCOM_GITHUB_PACKAGES_USER")?.Trim() ??
                Environment.GetEnvironmentVariable("GITHUB_ACTOR")?.Trim() ?? string.Empty;
            if (nuget.Length == 0 || github.Length == 0 || user.Length == 0)
            {
                throw new ReleaseFailureException(
                    "PublicationCredentialsMissing",
                    "Dual-feed publication requires explicit Supprocom NuGet.org and GitHub Packages credentials and a GitHub user name.");
            }

            return new PublicationCredentials(nuget, github, user);
        }

        private static string ReadSecret(string valueVariable, string fileVariable)
        {
            string value = Environment.GetEnvironmentVariable(valueVariable)?.Trim() ?? string.Empty;
            if (value.Length != 0)
            {
                return value;
            }

            string? configuredPath = Environment.GetEnvironmentVariable(fileVariable);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return string.Empty;
            }

            string path = Path.GetFullPath(configuredPath);
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ReleaseFailureException(
                    "PublicationCredentialUnreadable",
                    $"The credential file named by {fileVariable} is not readable.");
            }
        }
    }
}
