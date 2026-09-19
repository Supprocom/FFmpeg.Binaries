using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.FFmpeg.Release;

internal static class PlanWriter
{
    public static (string Hash, string Path) Write(string releaseRoot, ReleasePlan plan)
    {
        byte[] canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(plan, ReleaseJsonContext.Default.ReleasePlan);
        string hash = Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));
        string planDirectory = Path.Combine(releaseRoot, plan.PackageVersion, hash);
        Directory.CreateDirectory(planDirectory);
        string planPath = Path.Combine(planDirectory, "plan.json");
        var persisted = new PersistedPlan(hash, DateTimeOffset.UtcNow, plan);
        byte[] persistedBytes = JsonSerializer.SerializeToUtf8Bytes(persisted, ReleaseJsonContext.Default.PersistedPlan);

        if (File.Exists(planPath))
        {
            PersistedPlan existing = JsonSerializer.Deserialize(
                File.ReadAllBytes(planPath),
                ReleaseJsonContext.Default.PersistedPlan)
                ?? throw new ReleaseFailureException("InvalidPersistedPlan", "The existing release plan cannot be read.");
            byte[] existingCanonical = JsonSerializer.SerializeToUtf8Bytes(existing.Plan, ReleaseJsonContext.Default.ReleasePlan);
            string existingHash = Convert.ToHexStringLower(SHA256.HashData(existingCanonical));
            if (!existingHash.Equals(hash, StringComparison.Ordinal) ||
                !existing.PlanSha256.Equals(hash, StringComparison.Ordinal))
            {
                throw new ReleaseFailureException("PlanResumeMismatch", "The existing execution plan does not match the requested release.");
            }

            return (hash, planPath);
        }

        string temporaryPath = planPath + ".new";
        File.WriteAllBytes(temporaryPath, persistedBytes);
        File.Move(temporaryPath, planPath, overwrite: false);
        return (hash, planPath);
    }
}
