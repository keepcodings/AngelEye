using AngelEyeBmsBridge;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AngelEyeBmsBridge.UiTests;

public sealed class WorkerReleaseBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "angel-worker-release-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PackagerAndVerifier_AcceptPinnedSignedLinuxBundle()
    {
        using X509Certificate2 certificate = CreateCertificate("trusted");
        WorkerReleaseBundle bundle = await CreateBundleAsync(certificate);

        WorkerReleaseBundle verified = await WorkerReleaseVerifier.VerifyAsync(
            bundle.ArtifactPath,
            bundle.ManifestPath,
            bundle.SignaturePath,
            certificate);

        Assert.Equal("1.4.0", verified.Manifest.Version);
        Assert.Equal("a14aed3", verified.Manifest.BuildCommit);
        Assert.Equal("linux-x64", verified.Manifest.TargetRuntime);
        Assert.Equal("release.tar.gz", verified.Manifest.ArtifactFile);
        Assert.Equal(
            await WorkerReleaseVerifier.CalculateSha256Async(bundle.ArtifactPath),
            verified.Manifest.ArtifactSha256);
    }

    [Fact]
    public async Task Verifier_RejectsTamperedArtifactAndWrongCertificate()
    {
        using X509Certificate2 trusted = CreateCertificate("trusted");
        using X509Certificate2 attacker = CreateCertificate("attacker");
        WorkerReleaseBundle bundle = await CreateBundleAsync(trusted);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            WorkerReleaseVerifier.VerifyAsync(
                bundle.ArtifactPath,
                bundle.ManifestPath,
                bundle.SignaturePath,
                attacker));

        await File.AppendAllTextAsync(bundle.ArtifactPath, "tamper");
        await Assert.ThrowsAsync<CryptographicException>(() =>
            WorkerReleaseVerifier.VerifyAsync(
                bundle.ArtifactPath,
                bundle.ManifestPath,
                bundle.SignaturePath,
                trusted));
    }

    [Fact]
    public async Task Verifier_RejectsWrongRuntimeAndMalformedManifest()
    {
        using X509Certificate2 trusted = CreateCertificate("trusted");
        WorkerReleaseBundle bundle = await CreateBundleAsync(trusted);
        WorkerReleaseManifest wrongRuntime = bundle.Manifest with
        {
            TargetRuntime = "win-x64"
        };
        await WriteSignedManifestAsync(
            wrongRuntime,
            bundle.ManifestPath,
            bundle.SignaturePath,
            trusted);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WorkerReleaseVerifier.VerifyAsync(
                bundle.ArtifactPath,
                bundle.ManifestPath,
                bundle.SignaturePath,
                trusted));

        byte[] malformed = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"unknown":true}""");
        await File.WriteAllBytesAsync(bundle.ManifestPath, malformed);
        await File.WriteAllBytesAsync(
            bundle.SignaturePath,
            SignDetached(malformed, trusted));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WorkerReleaseVerifier.VerifyAsync(
                bundle.ArtifactPath,
                bundle.ManifestPath,
                bundle.SignaturePath,
                trusted));
    }

    private async Task<WorkerReleaseBundle> CreateBundleAsync(
        X509Certificate2 certificate)
    {
        string publish = Path.Combine(_root, "publish");
        string output = Path.Combine(_root, "output");
        Directory.CreateDirectory(publish);
        await File.WriteAllTextAsync(
            Path.Combine(publish, "angel-eye-bridge"),
            "#!/bin/sh\n");
        await File.WriteAllTextAsync(
            Path.Combine(publish, "angel-eye-bridge.dll"),
            "worker");

        return await WorkerReleasePackager.CreateAsync(
            publish,
            output,
            "1.4.0",
            "a14aed3",
            certificate,
            new DateTimeOffset(2026, 7, 23, 6, 0, 0, TimeSpan.Zero));
    }

    private static X509Certificate2 CreateCertificate(string commonName)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }

    private static async Task WriteSignedManifestAsync(
        WorkerReleaseManifest manifest,
        string manifestPath,
        string signaturePath,
        X509Certificate2 certificate)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(manifestPath, bytes);
        await File.WriteAllBytesAsync(
            signaturePath,
            SignDetached(bytes, certificate));
    }

    private static byte[] SignDetached(
        byte[] content,
        X509Certificate2 certificate)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(
            SubjectIdentifierType.IssuerAndSerialNumber,
            certificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly
        };
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
