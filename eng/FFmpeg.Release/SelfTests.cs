using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.FFmpeg.Release;

internal static class SelfTests
{
    public static void Run(string repositoryRoot)
    {
        string matrixPath = Path.Combine(repositoryRoot, "eng", "release-matrix.json");
        (ReleaseMatrix matrix, string matrixHash) = MatrixLoader.Load(matrixPath);
        Require(matrix.RuntimeIdentifiers.Count == 9, "runtime matrix cardinality");
        Require(matrixHash.Length == 64, "matrix hash length");
        VersionDefinition version = matrix.Versions.Single(item => item.Status == "approved");
        FlavorDefinition flavor = matrix.Flavors.Single();
        ReleasePlan first = Program.CreatePlan(matrix, matrixHash, version, flavor, new string('a', 40), reducedValidation: false);
        ReleasePlan second = Program.CreatePlan(matrix, matrixHash, version, flavor, new string('a', 40), reducedValidation: false);
        byte[] firstBytes = JsonSerializer.SerializeToUtf8Bytes(first, ReleaseJsonContext.Default.ReleasePlan);
        byte[] secondBytes = JsonSerializer.SerializeToUtf8Bytes(second, ReleaseJsonContext.Default.ReleasePlan);
        Require(SHA256.HashData(firstBytes).SequenceEqual(SHA256.HashData(secondBytes)), "deterministic plan hash");
        Require(first.PackageIds.Count == 12, "source, core, nine runtimes, and facade packages");
        Require(first.PackageIds[^1] == "Supprocom.FFmpeg.Binaries", "facade publishes last");
        Console.WriteLine("Self-tests passed: 5/5");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new ReleaseFailureException("SelfTestFailed", $"Release-program self-test failed: {label}.");
        }
    }
}
