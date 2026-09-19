using System.Security.Cryptography;
using System.Text;

namespace Supprocom.FFmpeg.Release;

internal sealed class SourceVerifier(HttpClient httpClient, ProcessRunner processRunner)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GpgTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(1);

    public async Task<SourceVerificationResult> VerifyAsync(
        VersionDefinition version,
        string officialSource,
        string planDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version.ArchiveUri);
        string inputDirectory = Path.Combine(planDirectory, "inputs");
        Directory.CreateDirectory(inputDirectory);

        string archivePath = Path.Combine(inputDirectory, $"ffmpeg-{version.Version}.tar.xz");
        string signaturePath = archivePath + ".asc";
        string keyPath = Path.Combine(inputDirectory, "ffmpeg-release-signing-key.asc");
        await DownloadAsync(new Uri(version.ArchiveUri!), archivePath, cancellationToken).ConfigureAwait(false);
        await DownloadAsync(new Uri(version.SignatureUri!), signaturePath, cancellationToken).ConfigureAwait(false);
        await DownloadAsync(new Uri(version.ReleaseKeyUri!), keyPath, cancellationToken).ConfigureAwait(false);

        string archiveHash = await HashFileAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (!archiveHash.Equals(version.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseFailureException("SourceHashMismatch", "The FFmpeg release archive hash does not match the approved matrix.");
        }

        string gpgHome = Path.Combine(planDirectory, "verification-keyring");
        Directory.CreateDirectory(gpgHome);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gpgHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string toolGpgHome = await ToToolPathAsync(gpgHome, planDirectory, cancellationToken).ConfigureAwait(false);
        string toolKeyPath = await ToToolPathAsync(keyPath, planDirectory, cancellationToken).ConfigureAwait(false);
        string toolSignaturePath = await ToToolPathAsync(signaturePath, planDirectory, cancellationToken).ConfigureAwait(false);
        string toolArchivePath = await ToToolPathAsync(archivePath, planDirectory, cancellationToken).ConfigureAwait(false);
        var gpgEnvironment = new Dictionary<string, string?> { ["GNUPGHOME"] = toolGpgHome };
        CommandResult inspectKey = await processRunner.RunAsync(
            "gpg",
            ["--batch", "--no-autostart", "--with-colons", "--import-options", "show-only", "--import", toolKeyPath],
            planDirectory,
            GpgTimeout,
            gpgEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(inspectKey, "ReleaseKeyInspectionFailed");
        HashSet<string> fingerprints = ParseFingerprints(inspectKey.StandardOutput);
        if (!fingerprints.Contains(version.ReleaseKeyFingerprint!, StringComparer.OrdinalIgnoreCase))
        {
            throw new ReleaseFailureException("ReleaseKeyMismatch", "The downloaded FFmpeg release key has an unexpected fingerprint.");
        }

        string verificationKeyring = Path.Combine(planDirectory, "ffmpeg-release-signing-key.gpg");
        string toolVerificationKeyring = await ToToolPathAsync(
            verificationKeyring,
            planDirectory,
            cancellationToken).ConfigureAwait(false);
        CommandResult createKeyring = await processRunner.RunAsync(
            "gpg",
            ["--batch", "--no-autostart", "--yes", "--dearmor", "--output", toolVerificationKeyring, toolKeyPath],
            planDirectory,
            GpgTimeout,
            gpgEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(createKeyring, "ReleaseKeyConversionFailed");

        CommandResult verify = await processRunner.RunAsync(
            "gpgv",
            ["--keyring", toolVerificationKeyring, "--status-fd=1", toolSignaturePath, toolArchivePath],
            planDirectory,
            GpgTimeout,
            gpgEnvironment,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(verify, "ReleaseSignatureInvalid");
        string signatureFingerprint = ParseValidSignature(verify.StandardOutput);
        if (!signatureFingerprint.Equals(version.ReleaseKeyFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseFailureException("ReleaseSignatureKeyMismatch", "The FFmpeg archive signature uses an unexpected key.");
        }

        await VerifyOfficialTagReferenceAsync(version, officialSource, planDirectory, cancellationToken).ConfigureAwait(false);
        var result = new SourceVerificationResult(
            archivePath,
            signaturePath,
            keyPath,
            archiveHash,
            signatureFingerprint,
            version.SourceCommit!,
            version.TagObject!);
        string resultPath = Path.Combine(planDirectory, "source-verification.json");
        await File.WriteAllBytesAsync(
            resultPath,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, ReleaseJsonContext.Default.SourceVerificationResult),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<string> ToToolPathAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken)
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
        EnsureSuccess(result, "VerificationPathConversionFailed");
        return result.StandardOutput.Trim();
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(DownloadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        string temporaryPath = destination + ".partial";
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var target = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             131072,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, linked.Token).ConfigureAwait(false);
                await target.FlushAsync(linked.Token).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destination, overwrite: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new ReleaseFailureException("SourceDownloadTimeout", $"Source download from '{uri.Host}' exceeded its fixed time limit.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task VerifyOfficialTagReferenceAsync(
        VersionDefinition version,
        string officialSource,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            "git",
            ["ls-remote", officialSource, $"refs/tags/{version.Tag}*"],
            workingDirectory,
            GitTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "OfficialTagLookupFailed");
        Dictionary<string, string> references = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        references.TryGetValue($"refs/tags/{version.Tag}", out string? tagObject);
        references.TryGetValue($"refs/tags/{version.Tag}^{{}}", out string? sourceCommit);
        if (tagObject is null ||
            sourceCommit is null ||
            !tagObject.Equals(version.TagObject, StringComparison.OrdinalIgnoreCase) ||
            !sourceCommit.Equals(version.SourceCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseFailureException(
                "OfficialTagMismatch",
                $"The official FFmpeg tag reference does not match the approved source identity " +
                $"(tag object: {tagObject ?? "missing"}; source commit: {sourceCommit ?? "missing"}).");
        }
    }

    private static HashSet<string> ParseFingerprints(string colonOutput) =>
        colonOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length > 9 && parts[0].Equals("fpr", StringComparison.Ordinal))
            .Select(parts => parts[9])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string ParseValidSignature(string statusOutput)
    {
        string? line = statusOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => value.StartsWith("[GNUPG:] VALIDSIG ", StringComparison.Ordinal));
        if (line is null)
        {
            throw new ReleaseFailureException("ReleaseSignatureInvalid", "GnuPG did not report a valid FFmpeg archive signature.");
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new ReleaseFailureException("ReleaseSignatureInvalid", "GnuPG returned an invalid signature status record.");
        }

        return parts[2];
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
            throw new ReleaseFailureException(
                code,
                $"A required public-source verification tool reported a failure.\n{diagnostic}");
        }
    }
}
