namespace Supprocom.FFmpeg.Release;

internal sealed class RepositoryGate(ProcessRunner processRunner)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> VerifyAsync(
        string repositoryRoot,
        string expectedOrigin,
        bool allowDirty,
        CancellationToken cancellationToken)
    {
        string actualRoot = await GitValueAsync(repositoryRoot, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (!Path.GetFullPath(actualRoot).Equals(
                Path.GetFullPath(repositoryRoot),
                StringComparison.Ordinal))
        {
            throw new ReleaseFailureException("RepositoryRootMismatch", "The release program is not running at the repository root.");
        }

        string actualOrigin = await GitValueAsync(repositoryRoot, ["remote", "get-url", "origin"], cancellationToken);
        if (!NormalizeGitUri(actualOrigin).Equals(NormalizeGitUri(expectedOrigin), StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseFailureException(
                "RepositoryIdentityMismatch",
                $"The origin remote is '{actualOrigin}', not the approved Supprocom repository.");
        }

        CommandResult status = await processRunner.RunAsync(
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"],
            repositoryRoot,
            GitTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(status, "GitStatusFailed");
        if (!allowDirty && !string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new ReleaseFailureException(
                "TrackedStateNotClean",
                "The stable release gate requires a completely clean tracked and untracked worktree.");
        }

        return await GitValueAsync(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken);
    }

    private async Task<string> GitValueAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        CommandResult result = await processRunner.RunAsync(
            "git",
            arguments,
            repositoryRoot,
            GitTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "GitCommandFailed");
        return result.StandardOutput.Trim();
    }

    private static void EnsureSuccess(CommandResult result, string code)
    {
        if (result.ExitCode != 0)
        {
            throw new ReleaseFailureException(code, "A required Git repository check failed.");
        }
    }

    private static string NormalizeGitUri(string value) =>
        value.Trim().TrimEnd('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
}
