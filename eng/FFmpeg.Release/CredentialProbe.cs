namespace Supprocom.FFmpeg.Release;

internal sealed record CredentialAvailability(bool NuGetOrg, bool GitHubPackages)
{
    public bool CanPublishBoth => NuGetOrg && GitHubPackages;
}

internal static class CredentialProbe
{
    public static CredentialAvailability InspectPresence()
    {
        bool nuget = HasEnvironmentValue("SUPPROCOM_NUGET_API_KEY") ||
                     HasReadableExplicitFile("SUPPROCOM_NUGET_API_KEY_FILE");
        bool github = HasEnvironmentValue("SUPPROCOM_GITHUB_PACKAGES_TOKEN") ||
                      HasReadableExplicitFile("SUPPROCOM_GITHUB_PACKAGES_TOKEN_FILE") ||
                      (HasEnvironmentValue("GITHUB_ACTIONS") && HasEnvironmentValue("GITHUB_TOKEN"));
        return new CredentialAvailability(nuget, github);
    }

    private static bool HasEnvironmentValue(string name) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    private static bool HasReadableExplicitFile(string variableName)
    {
        string? path = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
