namespace AngelEyeBmsBridge;

public readonly record struct DeploymentId
{
    private DeploymentId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Deployment ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }
    public static DeploymentId New() => new(Guid.NewGuid());
    public static DeploymentId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("D").ToLowerInvariant();
}

public enum WorkerDeploymentAction
{
    Preflight,
    Install,
    Rollback
}

public static class WorkerDeploymentCommandBuilder
{
    public const string ScriptPath = "/usr/local/sbin/angel-eye-worker-deploy";
    public const string StagingRoot = "/var/tmp/angel-eye-deploy";

    public static string StatusCommand =>
        $"sudo -n {ScriptPath} status";

    public static string Build(WorkerDeploymentAction action, DeploymentId deploymentId)
    {
        string actionName = action switch
        {
            WorkerDeploymentAction.Preflight => "preflight",
            WorkerDeploymentAction.Install => "install",
            WorkerDeploymentAction.Rollback => "rollback",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return $"sudo -n {ScriptPath} {actionName} {deploymentId}";
    }

    public static string StagingDirectory(DeploymentId deploymentId) =>
        $"{StagingRoot}/{deploymentId}";

    public static string RemoteArtifactPath(DeploymentId deploymentId) =>
        $"{StagingDirectory(deploymentId)}/release.tar.gz";

    public static string RemoteManifestPath(DeploymentId deploymentId) =>
        $"{StagingDirectory(deploymentId)}/release-manifest.json";

    public static string RemoteSignaturePath(DeploymentId deploymentId) =>
        $"{StagingDirectory(deploymentId)}/release-manifest.p7s";
}

public sealed record WorkerDeploymentAuthentication(
    string UserName,
    string PrivateKeyPath);

public sealed record WorkerDeploymentCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IWorkerDeploymentSessionFactory
{
    Task<IWorkerDeploymentSession> ConnectAsync(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        CancellationToken cancellationToken);
}

public interface IWorkerDeploymentSession : IAsyncDisposable
{
    Task<WorkerDeploymentCommandResult> GetStatusAsync(CancellationToken cancellationToken);
    Task UploadReleaseAsync(
        DeploymentId deploymentId,
        string artifactPath,
        string manifestPath,
        string signaturePath,
        CancellationToken cancellationToken);
    Task<WorkerDeploymentCommandResult> PreflightAsync(
        DeploymentId deploymentId,
        CancellationToken cancellationToken);
    Task<WorkerDeploymentCommandResult> InstallAsync(
        DeploymentId deploymentId,
        CancellationToken cancellationToken);
    Task<WorkerDeploymentCommandResult> RollbackAsync(
        DeploymentId deploymentId,
        CancellationToken cancellationToken);
}
