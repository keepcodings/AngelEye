using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AngelEyeBmsBridge;

public sealed record WorkerReleaseManifest(
    int SchemaVersion,
    string Version,
    string BuildCommit,
    string TargetRuntime,
    string ArtifactFile,
    string ArtifactSha256,
    DateTimeOffset CreatedUtc);

public sealed record WorkerReleaseBundle(
    WorkerReleaseManifest Manifest,
    string ArtifactPath,
    string ManifestPath,
    string SignaturePath);

public static partial class WorkerReleaseVerifier
{
    public const string RequiredRuntime = "linux-x64";
    public const string RequiredArtifactFile = "release.tar.gz";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<WorkerReleaseBundle> VerifyAsync(
        string artifactPath,
        string manifestPath,
        string signaturePath,
        X509Certificate2 trustedSigningCertificate,
        CancellationToken cancellationToken = default)
    {
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        byte[] signatureBytes = await File.ReadAllBytesAsync(signaturePath, cancellationToken);
        VerifyDetachedSignature(manifestBytes, signatureBytes, trustedSigningCertificate);

        WorkerReleaseManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WorkerReleaseManifest>(
                manifestBytes,
                JsonOptions)
                ?? throw new InvalidDataException("Release manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Release manifest JSON is invalid.", exception);
        }

        ValidateManifest(manifest, artifactPath);
        string actualDigest = await CalculateSha256Async(artifactPath, cancellationToken);
        if (!CryptographicEquals(actualDigest, manifest.ArtifactSha256))
        {
            throw new CryptographicException(
                $"Artifact digest mismatch. Expected {manifest.ArtifactSha256}, " +
                $"received {actualDigest}.");
        }

        return new WorkerReleaseBundle(
            manifest,
            artifactPath,
            manifestPath,
            signaturePath);
    }

    public static async Task<string> CalculateSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    internal static byte[] SignManifest(
        byte[] manifestBytes,
        X509Certificate2 signingCertificate)
    {
        if (!signingCertificate.HasPrivateKey)
        {
            throw new ArgumentException(
                "Signing certificate must contain a private key.",
                nameof(signingCertificate));
        }

        var cms = new SignedCms(new ContentInfo(manifestBytes), detached: true);
        var signer = new CmsSigner(
            SubjectIdentifierType.IssuerAndSerialNumber,
            signingCertificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly
        };
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    private static void VerifyDetachedSignature(
        byte[] manifestBytes,
        byte[] signatureBytes,
        X509Certificate2 trustedCertificate)
    {
        var cms = new SignedCms(new ContentInfo(manifestBytes), detached: true);
        try
        {
            cms.Decode(signatureBytes);
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException("Release manifest signature is invalid.", exception);
        }

        bool trustedSigner = cms.SignerInfos
            .Cast<SignerInfo>()
            .Any(signer =>
                signer.Certificate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    signer.Certificate.RawData,
                    trustedCertificate.RawData));
        if (!trustedSigner)
        {
            throw new CryptographicException(
                "Release manifest was not signed by the pinned certificate.");
        }
    }

    private static void ValidateManifest(
        WorkerReleaseManifest manifest,
        string artifactPath)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported release manifest schema.");
        }
        if (!VersionPattern().IsMatch(manifest.Version))
        {
            throw new InvalidDataException("Release version is invalid.");
        }
        if (!CommitPattern().IsMatch(manifest.BuildCommit))
        {
            throw new InvalidDataException("Release build commit is invalid.");
        }
        if (!string.Equals(
            manifest.TargetRuntime,
            RequiredRuntime,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release runtime must be {RequiredRuntime}.");
        }
        if (!string.Equals(
            manifest.ArtifactFile,
            RequiredArtifactFile,
            StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileName(artifactPath),
                RequiredArtifactFile,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release artifact filename must be {RequiredArtifactFile}.");
        }
        if (!DigestPattern().IsMatch(manifest.ArtifactSha256))
        {
            throw new InvalidDataException("Release artifact SHA-256 is invalid.");
        }
    }

    private static bool CryptographicEquals(string left, string right)
    {
        byte[] leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        byte[] rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();
}

public static class WorkerReleasePackager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<WorkerReleaseBundle> CreateAsync(
        string publishedWorkerDirectory,
        string outputDirectory,
        string version,
        string buildCommit,
        X509Certificate2 signingCertificate,
        DateTimeOffset? createdUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(publishedWorkerDirectory))
        {
            throw new DirectoryNotFoundException(publishedWorkerDirectory);
        }
        Directory.CreateDirectory(outputDirectory);

        string artifactPath = Path.Combine(
            outputDirectory,
            WorkerReleaseVerifier.RequiredArtifactFile);
        string manifestPath = Path.Combine(outputDirectory, "release-manifest.json");
        string signaturePath = Path.Combine(outputDirectory, "release-manifest.p7s");

        await CreateTarGzAsync(
            publishedWorkerDirectory,
            artifactPath,
            cancellationToken);
        string digest = await WorkerReleaseVerifier.CalculateSha256Async(
            artifactPath,
            cancellationToken);
        var manifest = new WorkerReleaseManifest(
            1,
            version,
            buildCommit,
            WorkerReleaseVerifier.RequiredRuntime,
            WorkerReleaseVerifier.RequiredArtifactFile,
            digest,
            createdUtc ?? DateTimeOffset.UtcNow);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            JsonOptions);
        byte[] signature = WorkerReleaseVerifier.SignManifest(
            manifestBytes,
            signingCertificate);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
        await File.WriteAllBytesAsync(signaturePath, signature, cancellationToken);

        return new WorkerReleaseBundle(
            manifest,
            artifactPath,
            manifestPath,
            signaturePath);
    }

    private static async Task CreateTarGzAsync(
        string sourceDirectory,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            artifactPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await using var gzip = new GZipStream(
            output,
            CompressionLevel.SmallestSize,
            leaveOpen: false);
        using var writer = new TarWriter(gzip, leaveOpen: false);

        foreach (string file in Directory.EnumerateFiles(
            sourceDirectory,
            "*",
            SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(sourceDirectory, file)
                .Replace('\\', '/');
            if (relativePath.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException("Published Worker path escaped its root.");
            }

            var entry = new PaxTarEntry(TarEntryType.RegularFile, relativePath)
            {
                DataStream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read)
            };
            writer.WriteEntry(entry);
            entry.DataStream.Dispose();
        }
    }
}
