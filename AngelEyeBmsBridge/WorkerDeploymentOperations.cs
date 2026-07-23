using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelEyeBmsBridge;

public sealed record WorkerDeploymentAuditEntry(
    Guid DeploymentId,
    string TargetId,
    string Host,
    string Operator,
    string ArtifactSha256,
    string? PreviousVersion,
    string? TargetVersion,
    string Stage,
    string Status,
    string Message,
    DateTimeOffset TimestampUtc,
    bool RollbackPerformed,
    string? ManualRecoveryPath,
    int? ExitCode = null);

public static class WorkerDeploymentProgressParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<WorkerDeploymentAuditEntry> ParseJsonLines(
        string jsonLines,
        WorkerDeploymentTarget target,
        string operatorName,
        string artifactSha256,
        int? exitCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);

        var entries = new List<WorkerDeploymentAuditEntry>();
        foreach (string line in jsonLines.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RemoteProgress value;
            try
            {
                value = JsonSerializer.Deserialize<RemoteProgress>(line, Options)
                    ?? throw new InvalidDataException("Deployment progress line is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Deployment progress JSON line is invalid.",
                    exception);
            }

            if (value.DeploymentId is null || value.DeploymentId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "Deployment progress must contain a non-empty deployment ID.");
            }
            if (string.IsNullOrWhiteSpace(value.Stage) ||
                string.IsNullOrWhiteSpace(value.Status) ||
                string.IsNullOrWhiteSpace(value.Message) ||
                value.TimestampUtc == default)
            {
                throw new InvalidDataException(
                    "Deployment progress is missing stage, status, message, or timestamp.");
            }
            if (string.IsNullOrWhiteSpace(value.Target) ||
                !string.Equals(
                    value.Target,
                    target.ExpectedInstanceName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Deployment progress target does not match the selected target.");
            }
            if (string.IsNullOrWhiteSpace(value.Operator) ||
                !string.Equals(value.Operator, operatorName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Deployment progress operator does not match the SSH operator.");
            }
            if (string.IsNullOrWhiteSpace(value.ArtifactSha256) ||
                !string.Equals(
                    value.ArtifactSha256,
                    artifactSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Deployment progress artifact digest does not match the selected release.");
            }

            entries.Add(new WorkerDeploymentAuditEntry(
                value.DeploymentId.Value,
                target.Id,
                target.Host,
                operatorName,
                artifactSha256,
                value.PreviousVersion,
                value.TargetVersion,
                value.Stage,
                value.Status,
                value.Message,
                value.TimestampUtc,
                value.RollbackPerformed,
                value.ManualRecoveryPath,
                exitCode));
        }
        return entries;
    }

    private sealed record RemoteProgress(
        Guid? DeploymentId,
        string? Target,
        string? Operator,
        string? ArtifactSha256,
        string Stage,
        string Status,
        string Message,
        DateTimeOffset TimestampUtc,
        string? PreviousVersion,
        string? TargetVersion,
        bool RollbackPerformed,
        string? ManualRecoveryPath);
}

public interface IWorkerDeploymentAuditStore
{
    Task<bool> AppendAsync(
        WorkerDeploymentAuditEntry entry,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkerDeploymentAuditEntry>> ReadAllAsync(
        CancellationToken cancellationToken);
}

public sealed class FileWorkerDeploymentAuditStore : IWorkerDeploymentAuditStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _knownEntries =
        new(StringComparer.Ordinal);
    private bool _loaded;

    public FileWorkerDeploymentAuditStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AngelEye",
            "deployment-audit",
            "worker-deployments.jsonl");

    public async Task<bool> AppendAsync(
        WorkerDeploymentAuditEntry entry,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            string key = GetKey(entry);
            if (!_knownEntries.TryAdd(key, 0))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch
        {
            _knownEntries.TryRemove(GetKey(entry), out _);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkerDeploymentAuditEntry>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadEntriesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }
        foreach (WorkerDeploymentAuditEntry entry in
            await ReadEntriesAsync(cancellationToken))
        {
            _knownEntries.TryAdd(GetKey(entry), 0);
        }
        _loaded = true;
    }

    private async Task<IReadOnlyList<WorkerDeploymentAuditEntry>> ReadEntriesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<WorkerDeploymentAuditEntry>();
        }

        var entries = new List<WorkerDeploymentAuditEntry>();
        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            entries.Add(
                JsonSerializer.Deserialize<WorkerDeploymentAuditEntry>(line)
                ?? throw new InvalidDataException("Local deployment audit line is empty."));
        }
        return entries;
    }

    private static string GetKey(WorkerDeploymentAuditEntry entry) =>
        string.Join(
            "|",
            entry.DeploymentId.ToString("D"),
            entry.TargetId,
            entry.Stage,
            entry.Status,
            entry.TimestampUtc.ToUniversalTime().ToString("O"),
            entry.Message);
}

public sealed record WorkerDeploymentRolloutDecision(bool Allowed, string Message)
{
    public static WorkerDeploymentRolloutDecision Permit(string message) =>
        new(true, message);
    public static WorkerDeploymentRolloutDecision Deny(string message) =>
        new(false, message);
}

public static class WorkerDeploymentRolloutPolicy
{
    public static WorkerDeploymentRolloutDecision Evaluate(
        WorkerDeploymentTarget target,
        WorkerDeploymentStatus targetStatus,
        string artifactSha256,
        IReadOnlyCollection<WorkerDeploymentAuditEntry> audit,
        WorkerDeploymentStatus? standbyStatus)
    {
        if (!targetStatus.Matches(target))
        {
            return WorkerDeploymentRolloutDecision.Deny(
                "遠端身分、角色或服務狀態與固定目標不符。");
        }
        if (!IsSha256(artifactSha256))
        {
            return WorkerDeploymentRolloutDecision.Deny("Artifact SHA-256 格式錯誤。");
        }
        if (target.Id == "qa-29")
        {
            return WorkerDeploymentRolloutDecision.Permit("QA 目標可執行驗證部署。");
        }

        bool passedQa = audit.Any(entry =>
            entry.TargetId == "qa-29" &&
            string.Equals(
                entry.ArtifactSha256,
                artifactSha256,
                StringComparison.Ordinal) &&
            entry.Stage == "health" &&
            entry.Status == "succeeded" &&
            !entry.RollbackPerformed);
        if (!passedQa)
        {
            return WorkerDeploymentRolloutDecision.Deny(
                "相同 artifact digest 尚無 QA .29 成功紀錄。");
        }

        if (target.Id == "production-30")
        {
            WorkerDeploymentTarget standby =
                WorkerDeploymentTargets.Get("standby-31");
            if (standbyStatus is null || !standbyStatus.Matches(standby))
            {
                return WorkerDeploymentRolloutDecision.Deny(
                    "無法證明 .31 身分且服務為 disabled/inactive。");
            }
        }

        return WorkerDeploymentRolloutDecision.Permit(
            target.IsStandby
                ? "QA gate 通過；安裝後必須維持 disabled/inactive。"
                : "QA gate 與 Standby 狀態均已通過。");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record WorkerDeploymentPlan(
    DeploymentId DeploymentId,
    WorkerDeploymentTarget Target,
    WorkerReleaseBundle Release,
    string Operator,
    WorkerDeploymentStatus StatusBefore);

public sealed record WorkerDeploymentOperationResult(
    DeploymentId DeploymentId,
    WorkerDeploymentStatus Status,
    IReadOnlyList<WorkerDeploymentAuditEntry> Progress,
    bool RemoteResultKnown);

public sealed class WorkerDeploymentCoordinator
{
    private readonly IWorkerDeploymentSessionFactory _sessionFactory;
    private readonly IWorkerDeploymentAuditStore _auditStore;

    public WorkerDeploymentCoordinator(
        IWorkerDeploymentSessionFactory sessionFactory,
        IWorkerDeploymentAuditStore auditStore)
    {
        _sessionFactory = sessionFactory;
        _auditStore = auditStore;
    }

    public async Task<WorkerDeploymentStatus> GetStatusAsync(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        CancellationToken cancellationToken)
    {
        await using IWorkerDeploymentSession session =
            await _sessionFactory.ConnectAsync(
                target,
                authentication,
                cancellationToken);
        WorkerDeploymentCommandResult result =
            await session.GetStatusAsync(cancellationToken);
        if (!result.Succeeded)
        {
            throw RemoteFailure("status", result);
        }
        WorkerDeploymentStatus status =
            WorkerDeploymentStatusParser.Parse(result.StandardOutput);
        if (!status.Matches(target))
        {
            throw new InvalidDataException(
                $"{target.DisplayName} 遠端身分或角色與 allow-list 不符。");
        }
        return status;
    }

    public async Task<WorkerDeploymentPlan> PreflightAsync(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        WorkerReleaseBundle release,
        string operatorName,
        CancellationToken cancellationToken)
    {
        WorkerDeploymentStatus targetStatus = await GetStatusAsync(
            target,
            authentication,
            cancellationToken);
        WorkerDeploymentStatus? standbyStatus = null;
        if (target.Id == "production-30")
        {
            standbyStatus = await GetStatusAsync(
                WorkerDeploymentTargets.Get("standby-31"),
                authentication,
                cancellationToken);
        }

        IReadOnlyList<WorkerDeploymentAuditEntry> audit =
            await _auditStore.ReadAllAsync(cancellationToken);
        WorkerDeploymentRolloutDecision decision =
            WorkerDeploymentRolloutPolicy.Evaluate(
                target,
                targetStatus,
                release.Manifest.ArtifactSha256,
                audit,
                standbyStatus);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Message);
        }

        DeploymentId deploymentId = DeploymentId.New();
        await using IWorkerDeploymentSession session =
            await _sessionFactory.ConnectAsync(
                target,
                authentication,
                cancellationToken);
        await session.UploadReleaseAsync(
            deploymentId,
            release.ArtifactPath,
            release.ManifestPath,
            release.SignaturePath,
            cancellationToken);
        WorkerDeploymentCommandResult result =
            await session.PreflightAsync(deploymentId, cancellationToken);
        IReadOnlyList<WorkerDeploymentAuditEntry> progress =
            WorkerDeploymentProgressParser.ParseJsonLines(
                result.StandardOutput,
                target,
                operatorName,
                release.Manifest.ArtifactSha256,
                result.ExitCode);
        await AppendAllAsync(progress, cancellationToken);
        if (!result.Succeeded ||
            !progress.Any(entry =>
                entry.Stage == "preflight" && entry.Status == "succeeded"))
        {
            throw RemoteFailure("preflight", result);
        }

        return new WorkerDeploymentPlan(
            deploymentId,
            target,
            release,
            operatorName,
            targetStatus);
    }

    public async Task<WorkerDeploymentOperationResult> InstallAsync(
        WorkerDeploymentPlan plan,
        WorkerDeploymentAuthentication authentication,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkerDeploymentAuditEntry> audit =
            await _auditStore.ReadAllAsync(cancellationToken);
        bool preflightSucceeded = audit.Any(entry =>
            entry.DeploymentId == plan.DeploymentId.Value &&
            entry.TargetId == plan.Target.Id &&
            entry.ArtifactSha256 == plan.Release.Manifest.ArtifactSha256 &&
            entry.Stage == "preflight" &&
            entry.Status == "succeeded");
        if (!preflightSucceeded)
        {
            throw new InvalidOperationException(
                "找不到這個 deployment ID 的成功 preflight 紀錄。");
        }
        if (audit.Any(entry =>
            entry.DeploymentId == plan.DeploymentId.Value &&
            entry.Stage == "install-command"))
        {
            throw new InvalidOperationException(
                "此 deployment ID 已送出 install；請使用狀態恢復，不得重送。");
        }

        var started = new WorkerDeploymentAuditEntry(
            plan.DeploymentId.Value,
            plan.Target.Id,
            plan.Target.Host,
            plan.Operator,
            plan.Release.Manifest.ArtifactSha256,
            plan.StatusBefore.CurrentVersion,
            plan.Release.Manifest.Version,
            "install-command",
            "started",
            "Remote install command submitted.",
            DateTimeOffset.UtcNow,
            false,
            null);
        await _auditStore.AppendAsync(started, cancellationToken);

        IReadOnlyList<WorkerDeploymentAuditEntry> progress = [started];
        try
        {
            await using IWorkerDeploymentSession session =
                await _sessionFactory.ConnectAsync(
                    plan.Target,
                    authentication,
                    cancellationToken);
            WorkerDeploymentCommandResult result =
                await session.InstallAsync(plan.DeploymentId, cancellationToken);
            IReadOnlyList<WorkerDeploymentAuditEntry> remoteProgress =
                WorkerDeploymentProgressParser.ParseJsonLines(
                    result.StandardOutput,
                    plan.Target,
                    plan.Operator,
                    plan.Release.Manifest.ArtifactSha256,
                    result.ExitCode);
            await AppendAllAsync(remoteProgress, cancellationToken);
            progress = [started, .. remoteProgress];

            WorkerDeploymentStatus status = await GetStatusAsync(
                plan.Target,
                authentication,
                cancellationToken);
            if (plan.Target.IsStandby &&
                (status.ServiceActive || status.ServiceEnabled))
            {
                throw new InvalidDataException(
                    "Standby 安裝後不是 disabled/inactive，部署視為失敗。");
            }
            return new WorkerDeploymentOperationResult(
                plan.DeploymentId,
                status,
                progress,
                RemoteResultKnown: true);
        }
        catch (OperationCanceledException)
        {
            await AppendUnknownResultAsync(plan, "GUI cancelled or disconnected.");
            throw;
        }
        catch (Exception)
        {
            await AppendUnknownResultAsync(plan, "SSH disconnected; recover status before any further action.");
            throw;
        }
    }

    public async Task<WorkerDeploymentOperationResult> RecoverAsync(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        DeploymentId deploymentId,
        CancellationToken cancellationToken)
    {
        WorkerDeploymentStatus status = await GetStatusAsync(
            target,
            authentication,
            cancellationToken);
        IReadOnlyList<WorkerDeploymentAuditEntry> audit =
            await _auditStore.ReadAllAsync(cancellationToken);
        IReadOnlyList<WorkerDeploymentAuditEntry> progress = audit
            .Where(entry =>
                entry.DeploymentId == deploymentId.Value &&
                entry.TargetId == target.Id)
            .OrderBy(entry => entry.TimestampUtc)
            .ToArray();
        bool known =
            status.LastDeploymentId == deploymentId.Value &&
            !string.IsNullOrWhiteSpace(status.LastResult);
        return new WorkerDeploymentOperationResult(
            deploymentId,
            status,
            progress,
            known);
    }

    public async Task<WorkerDeploymentOperationResult> RollbackAsync(
        WorkerDeploymentTarget target,
        WorkerDeploymentAuthentication authentication,
        DeploymentId deploymentId,
        string operatorName,
        string artifactSha256,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkerDeploymentAuditEntry> audit =
            await _auditStore.ReadAllAsync(cancellationToken);
        if (!audit.Any(entry =>
            entry.DeploymentId == deploymentId.Value &&
            entry.TargetId == target.Id))
        {
            throw new InvalidOperationException(
                "Manual rollback 只能指定本機 audit 已記錄的 deployment ID。");
        }

        await using IWorkerDeploymentSession session =
            await _sessionFactory.ConnectAsync(
                target,
                authentication,
                cancellationToken);
        WorkerDeploymentCommandResult result =
            await session.RollbackAsync(deploymentId, cancellationToken);
        IReadOnlyList<WorkerDeploymentAuditEntry> progress =
            WorkerDeploymentProgressParser.ParseJsonLines(
                result.StandardOutput,
                target,
                operatorName,
                artifactSha256,
                result.ExitCode);
        await AppendAllAsync(progress, cancellationToken);
        WorkerDeploymentStatus status = await GetStatusAsync(
            target,
            authentication,
            cancellationToken);
        return new WorkerDeploymentOperationResult(
            deploymentId,
            status,
            progress,
            RemoteResultKnown: result.Succeeded);
    }

    private async Task AppendAllAsync(
        IEnumerable<WorkerDeploymentAuditEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (WorkerDeploymentAuditEntry entry in entries)
        {
            await _auditStore.AppendAsync(entry, cancellationToken);
        }
    }

    private Task AppendUnknownResultAsync(
        WorkerDeploymentPlan plan,
        string message) =>
        _auditStore.AppendAsync(
            new WorkerDeploymentAuditEntry(
                plan.DeploymentId.Value,
                plan.Target.Id,
                plan.Target.Host,
                plan.Operator,
                plan.Release.Manifest.ArtifactSha256,
                plan.StatusBefore.CurrentVersion,
                plan.Release.Manifest.Version,
                "install-command",
                "unknown",
                message,
                DateTimeOffset.UtcNow,
                false,
                null),
            CancellationToken.None);

    private static InvalidOperationException RemoteFailure(
        string action,
        WorkerDeploymentCommandResult result) =>
        new(
            $"Remote {action} failed with exit code {result.ExitCode}: " +
            $"{result.StandardError}");
}
