using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
        byte[] unsigned = CreateTestPackage("payload", signature: null);
        byte[] repositorySigned = CreateTestPackage("payload", signature: "repository-signature");
        byte[] changed = CreateTestPackage("changed-payload", signature: "repository-signature");
        string unsignedIdentity = PackagePublisher.ComputePackageContentIdentity(unsigned);
        Require(
            unsignedIdentity == PackagePublisher.ComputePackageContentIdentity(repositorySigned),
            "repository signature does not change package content identity");
        Require(
            unsignedIdentity != PackagePublisher.ComputePackageContentIdentity(changed),
            "package content identity detects changed payload bytes");
        Console.WriteLine("Self-tests passed: 7/7");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new ReleaseFailureException("SelfTestFailed", $"Release-program self-test failed: {label}.");
        }
    }

    private static byte[] CreateTestPackage(string payload, string? signature)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "test.nuspec", "<package />");
            WriteEntry(archive, "runtimes/test/native/ffmpeg/ffmpeg", payload);
            if (signature is not null)
            {
                WriteEntry(archive, ".signature.p7s", signature);
            }
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }
}
