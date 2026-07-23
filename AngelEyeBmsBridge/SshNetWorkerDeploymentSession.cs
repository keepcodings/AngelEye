using Renci.SshNet;
using Renci.SshNet.Common;

namespace AngelEyeBmsBridge;

public sealed class SshNetWorkerDeploymentSessionFactory : IWorkerDeploymentSessionFactory
{
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _commandTimeout;

    public SshNetWorkerDeploymentSessionFactory(
        TimeSpan? connectTimeout = null,
        TimeSpan? commandTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15);
        _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<IWorkerDeploymentSession> ConnectAsync(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        CancellationToken cancellationToken)
    {
        if (!target.HasPinnedFingerprint)
        {
            throw new InvalidOperationException(
                $"{target.DisplayName} 尚未配置 SSH host fingerprint，拒絕連線。");
        }
        if (string.IsNullOrWhiteSpace(authentication.UserName))
        {
            throw new ArgumentException("SSH user name is required.", nameof(authentication));
        }
        if (string.IsNullOrWhiteSpace(authentication.PrivateKeyPath) ||
            !File.Exists(authentication.PrivateKeyPath))
        {
            throw new FileNotFoundException(
                "SSH private key file was not found.",
                authentication.PrivateKeyPath);
        }

        var privateKey = new PrivateKeyFile(authentication.PrivateKeyPath);
        var connectionInfo = new ConnectionInfo(
            target.Host,
            target.SshPort,
            authentication.UserName,
            new PrivateKeyAuthenticationMethod(authentication.UserName, privateKey))
        {
            Timeout = _connectTimeout
        };
        var ssh = new SshClient(connectionInfo);
        ConfigureHostKeyPinning(ssh, target);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            await ssh.ConnectAsync(timeout.Token);
            return new SshNetWorkerDeploymentSession(
                target,
                authentication,
                ssh,
                privateKey,
                _connectTimeout,
                _commandTimeout);
        }
        catch
        {
            ssh.Dispose();
            privateKey.Dispose();
            throw;
        }
    }

    internal static void ConfigureHostKeyPinning(
        BaseClient client,
        WorkerDeploymentTarget target)
    {
        client.HostKeyReceived += (_, args) =>
        {
            string actual = $"SHA256:{args.FingerPrintSHA256}";
            args.CanTrust = WorkerDeploymentHostKeyVerifier.IsTrusted(
                target.ExpectedFingerprint,
                actual);
            if (!args.CanTrust)
            {
                throw new SshConnectionException(
                    $"{target.DisplayName} SSH host fingerprint mismatch. " +
                    $"Expected {target.ExpectedFingerprint}, received {actual}.");
            }
        };
    }

}

public static class WorkerDeploymentHostKeyVerifier
{
    public static bool IsTrusted(string expectedFingerprint, string actualFingerprint)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint) ||
            string.IsNullOrWhiteSpace(actualFingerprint))
        {
            return false;
        }

        byte[] expected = System.Text.Encoding.UTF8.GetBytes(expectedFingerprint);
        byte[] actual = System.Text.Encoding.UTF8.GetBytes(actualFingerprint);
        return expected.Length == actual.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                expected,
                actual);
    }
}

internal sealed class SshNetWorkerDeploymentSession : IWorkerDeploymentSession
{
    private readonly WorkerDeploymentTarget _target;
    private readonly WorkerDeploymentAuthentication _authentication;
    private readonly SshClient _ssh;
    private readonly PrivateKeyFile _privateKey;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _commandTimeout;
    private bool _disposed;

    public SshNetWorkerDeploymentSession(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        SshClient ssh,
        PrivateKeyFile privateKey,
        TimeSpan connectTimeout,
        TimeSpan commandTimeout)
    {
        _target = target;
        _authentication = authentication;
        _ssh = ssh;
        _privateKey = privateKey;
        _connectTimeout = connectTimeout;
        _commandTimeout = commandTimeout;
    }

    public Task<WorkerDeploymentCommandResult> GetStatusAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(WorkerDeploymentCommandBuilder.StatusCommand, cancellationToken);

    public async Task UploadReleaseAsync(
        DeploymentId deploymentId,
        string artifactPath,
        string manifestPath,
        string signaturePath,
        CancellationToken cancellationToken)
    {
        EnsureReadableFile(artifactPath);
        EnsureReadableFile(manifestPath);
        EnsureReadableFile(signaturePath);

        WorkerDeploymentCommandResult createDirectory = await ExecuteAsync(
            $"sudo -n {WorkerDeploymentCommandBuilder.ScriptPath} preflight {deploymentId}",
            cancellationToken);
        if (!createDirectory.Succeeded)
        {
            throw new InvalidOperationException(
                $"Remote preflight failed: {createDirectory.StandardError}");
        }

        using var key = new PrivateKeyFile(_authentication.PrivateKeyPath);
        var connectionInfo = new ConnectionInfo(
            _target.Host,
            _target.SshPort,
            _authentication.UserName,
            new PrivateKeyAuthenticationMethod(_authentication.UserName, key))
        {
            Timeout = _connectTimeout
        };
        using var sftp = new SftpClient(connectionInfo);
        SshNetWorkerDeploymentSessionFactory.ConfigureHostKeyPinning(sftp, _target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        await sftp.ConnectAsync(timeout.Token);

        await UploadOneAsync(
            sftp,
            artifactPath,
            WorkerDeploymentCommandBuilder.RemoteArtifactPath(deploymentId),
            timeout.Token);
        await UploadOneAsync(
            sftp,
            manifestPath,
            WorkerDeploymentCommandBuilder.RemoteManifestPath(deploymentId),
            timeout.Token);
        await UploadOneAsync(
            sftp,
            signaturePath,
            WorkerDeploymentCommandBuilder.RemoteSignaturePath(deploymentId),
            timeout.Token);
    }

    public Task<WorkerDeploymentCommandResult> PreflightAsync(
        DeploymentId deploymentId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            WorkerDeploymentCommandBuilder.Build(
                WorkerDeploymentAction.Preflight,
                deploymentId),
            cancellationToken);

    public Task<WorkerDeploymentCommandResult> InstallAsync(
        DeploymentId deploymentId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            WorkerDeploymentCommandBuilder.Build(
                WorkerDeploymentAction.Install,
                deploymentId),
            cancellationToken);

    public Task<WorkerDeploymentCommandResult> RollbackAsync(
        DeploymentId deploymentId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            WorkerDeploymentCommandBuilder.Build(
                WorkerDeploymentAction.Rollback,
                deploymentId),
            cancellationToken);

    private async Task<WorkerDeploymentCommandResult> ExecuteAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using SshCommand command = _ssh.CreateCommand(commandText);
        command.CommandTimeout = _commandTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        await command.ExecuteAsync(timeout.Token);
        return new WorkerDeploymentCommandResult(
            command.ExitStatus ?? -1,
            command.Result ?? string.Empty,
            command.Error ?? string.Empty);
    }

    private static async Task UploadOneAsync(
        SftpClient sftp,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        await Task.Run(
            () => sftp.UploadFile(stream, remotePath, canOverride: true),
            cancellationToken);
    }

    private static void EnsureReadableFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Deployment release file was not found.", path);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ssh.IsConnected)
            {
                _ssh.Disconnect();
            }
            _ssh.Dispose();
            _privateKey.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
