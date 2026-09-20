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
        Require(matrix.SchemaVersion == 2, "compatibility-policy matrix schema");
        Require(matrix.RuntimeIdentifiers.Count == 9, "runtime matrix cardinality");
        Require(matrixHash.Length == 64, "matrix hash length");
        Require(
            MatrixLoader.ComputeCanonicalSha256("{\n  \"value\": 1\n}\n"u8) ==
            MatrixLoader.ComputeCanonicalSha256("{\r\n  \"value\": 1\r\n}\r\n"u8),
            "platform-independent matrix hash");
        VersionDefinition version = matrix.Versions.Single(item => item.Status == "approved");
        FlavorDefinition flavor = matrix.Flavors.Single();
        ReleasePlan first = Program.CreatePlan(matrix, matrixHash, version, flavor, new string('a', 40), reducedValidation: false);
        ReleasePlan second = Program.CreatePlan(matrix, matrixHash, version, flavor, new string('a', 40), reducedValidation: false);
        byte[] firstBytes = JsonSerializer.SerializeToUtf8Bytes(first, ReleaseJsonContext.Default.ReleasePlan);
        byte[] secondBytes = JsonSerializer.SerializeToUtf8Bytes(second, ReleaseJsonContext.Default.ReleasePlan);
        Require(SHA256.HashData(firstBytes).SequenceEqual(SHA256.HashData(secondBytes)), "deterministic plan hash");
        Require(firstBytes.AsSpan().IndexOf("\r\n"u8) < 0, "platform-independent JSON newlines");
        Require(first.PackageIds.Count == 12, "source, core, nine runtimes, and facade packages");
        Require(first.PackageIds[^1] == "Supprocom.FFmpeg.Binaries", "facade publishes last");
        string sourceNuspec = PackageAssembler.CreateNuspec(
            "Supprocom.FFmpeg.Source",
            first,
            "Complete corresponding source.",
            [],
            [("FFmpeg-LICENSE.md", "licenses")],
            "licenses/FFmpeg-LICENSE.md");
        Require(
            sourceNuspec.Contains(
                "<license type=\"file\">licenses/FFmpeg-LICENSE.md</license>",
                StringComparison.Ordinal) &&
            !sourceNuspec.Contains(
                "<license type=\"expression\">LGPL-2.1-or-later</license>",
                StringComparison.Ordinal),
            "mixed-license source package metadata");
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
        Require(NativeWorker.IsMacSoname("libavcodec.63.dylib", "libavcodec"), "macOS major-version SONAME detection");
        Require(!NativeWorker.IsMacSoname("libavcodec.63.1.100.dylib", "libavcodec"), "macOS full-version alias rejection");
        Require(!NativeWorker.IsMacSoname("libavcodec.dylib", "libavcodec"), "macOS unversioned alias rejection");
        string windowsDependencies = NativeWorker.CanonicalizeWindowsDependencyEvidence(
            "first-build/ffmpeg.exe: file format pei-x86-64\n" +
            "  DLL Name: KERNEL32.dll\n" +
            "  DLL Name: avcodec-63.dll\n" +
            "  DLL Name: kernel32.DLL\n");
        Require(
            windowsDependencies == "DLL Name: avcodec-63.dll\nDLL Name: KERNEL32.dll",
            "canonical Windows dependency evidence");
        Require(
            NativeWorker.IsBundledWindowsToolchainRuntime("libgcc_s_dw2-1.dll") &&
            NativeWorker.IsBundledWindowsToolchainRuntime("LIBWINPTHREAD-1.DLL") &&
            !NativeWorker.IsBundledWindowsToolchainRuntime("KERNEL32.dll"),
            "Windows toolchain runtime classification");
        RuntimeDefinition linux = matrix.RuntimeIdentifiers.Single(item => item.Rid == "linux-x64");
        Require(
            linux.MinimumOsVersion == "24.04" &&
            linux.Libc == "glibc" &&
            linux.MinimumLibcVersion == "2.39",
            "glibc compatibility boundary");
        RuntimeDefinition musl = matrix.RuntimeIdentifiers.Single(item => item.Rid == "linux-musl-arm64");
        Require(
            musl.MinimumOsVersion == "3.23" &&
            musl.MinimumLibcVersion == "1.2.5" &&
            musl.WorkerImage?.Contains("@sha256:", StringComparison.Ordinal) == true,
            "musl compatibility boundary");
        RuntimeDefinition mac = matrix.RuntimeIdentifiers.Single(item => item.Rid == "osx-x64");
        Require(
            mac.MinimumOsVersion == "15.0" &&
            mac.SystemDependencies?.Contains("/usr/lib/libSystem.B.dylib", StringComparer.Ordinal) == true,
            "macOS compatibility boundary");
        Require(
            NativeWorker.ParseElfDependencies(
                "0x1 (NEEDED) Shared library: [libm.so.6]\n0x1 (NEEDED) Shared library: [libc.so.6]\n")
                .SequenceEqual(["libc.so.6", "libm.so.6"], StringComparer.Ordinal),
            "ELF dependency parsing");
        Require(
            NativeWorker.ParseMacDependencies(
                "/tmp/ffmpeg:\n\t@rpath/libavutil.61.dylib (compatibility version 61.0.0, current version 61.1.0)\n" +
                "\t/usr/lib/libSystem.B.dylib (compatibility version 1.0.0, current version 1.0.0)\n")
                .SequenceEqual(["/usr/lib/libSystem.B.dylib", "@rpath/libavutil.61.dylib"], StringComparer.Ordinal),
            "Mach-O dependency parsing");
        Require(
            NativeWorker.ParseGlibcVersions("GLIBC_2.2.5 GLIBC_2.39 GLIBC_2.17 GLIBC_2.39")
                .SequenceEqual(["2.2.5", "2.17", "2.39"], StringComparer.Ordinal),
            "glibc symbol-version parsing");
        Require(
            NativeWorker.ParseMacMinimumOsVersions(
                "cmd LC_BUILD_VERSION\n      minos 15.0\n        sdk 15.5\n" +
                "cmd LC_SOURCE_VERSION\n    version 0.0\ncurrent version 63.1.0\n")
                .SequenceEqual(["15.0"], StringComparer.Ordinal),
            "Mach-O minimum-OS parsing");
        Require(
            NativeWorker.CompareDottedVersions("15.0.0", "15.0") == 0 &&
            NativeWorker.CompareDottedVersions("2.39", "2.35") > 0,
            "numeric compatibility-version ordering");
        Require(
            NativeWorker.ReadOsReleaseValue("ID=ubuntu\nVERSION_ID=\"24.04\"\n", "VERSION_ID") == "24.04",
            "OS release parsing");
        Console.WriteLine("Self-tests passed: 25/25");
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
