using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBmsBridge.UiTests;

public sealed class WorkerDeploymentOperationsTests
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("qa-29", false, false, true)]
    [InlineData("production-30", false, true, false)]
    [InlineData("production-30", true, false, false)]
    [InlineData("production-30", true, true, true)]
    [InlineData("standby-31", false, true, false)]
    [InlineData("standby-31", true, true, true)]
    public void RolloutPolicy_FollowsQaDigestAndStandbyGate(
        string targetId,
        bool hasQaEvidence,
        bool standbyIsSafe,
        bool expected)
    {
        WorkerDeploymentTarget target = WorkerDeploymentTargets.Get(targetId);
        WorkerDeploymentStatus targetStatus = StatusFor(target, standbySafe: true);
        WorkerDeploymentStatus standbyStatus = StatusFor(
            WorkerDeploymentTargets.Get("standby-31"),
            standbyIsSafe);
        IReadOnlyCollection<WorkerDeploymentAuditEntry> audit =
            hasQaEvidence
                ? [SuccessfulQaEntry(Digest)]
                : Array.Empty<WorkerDeploymentAuditEntry>();

        WorkerDeploymentRolloutDecision decision =
            WorkerDeploymentRolloutPolicy.Evaluate(
                target,
                targetStatus,
                Digest,
                audit,
                standbyStatus);

        Assert.Equal(expected, decision.Allowed);
    }

    [Fact]
    public void RolloutPolicy_RejectsWrongDigestAndUnknownIdentityWithoutOverride()
    {
        WorkerDeploymentTarget production =
            WorkerDeploymentTargets.Get("production-30");
        WorkerDeploymentStatus safeStandby = StatusFor(
            WorkerDeploymentTargets.Get("standby-31"),
            standbySafe: true);

        Assert.False(WorkerDeploymentRolloutPolicy.Evaluate(
            production,
            StatusFor(production, standbySafe: true),
            Digest,
            [SuccessfulQaEntry(new string('a', 64))],
            safeStandby).Allowed);

        WorkerDeploymentStatus wrongIdentity =
            StatusFor(production, standbySafe: true) with
            {
                InstanceName = "unknown-host"
            };
        Assert.False(WorkerDeploymentRolloutPolicy.Evaluate(
            production,
            wrongIdentity,
            Digest,
            [SuccessfulQaEntry(Digest)],
            safeStandby).Allowed);
    }

    [Fact]
    public void ProgressParser_RequiresCompleteMatchingStructuredFields()
    {
        WorkerDeploymentTarget qa = WorkerDeploymentTargets.Get("qa-29");
        const string line = """
            {"deploymentId":"4f97045b-ec5d-42c8-8c82-b45c97a1fa92","target":"telebet-29","operator":"deploy","artifactSha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","stage":"health","status":"succeeded","message":"Worker release is healthy","timestampUtc":"2026-07-23T06:00:00Z","previousVersion":"1.3.0","targetVersion":"1.4.0","rollbackPerformed":false,"manualRecoveryPath":null}
            """;

        WorkerDeploymentAuditEntry entry = Assert.Single(
            WorkerDeploymentProgressParser.ParseJsonLines(
                line,
                qa,
                "deploy",
                Digest,
                0));

        Assert.Equal(qa.Id, entry.TargetId);
        Assert.Equal("deploy", entry.Operator);
        Assert.Equal(Digest, entry.ArtifactSha256);
        Assert.Equal("health", entry.Stage);
        Assert.Equal(0, entry.ExitCode);

        Assert.Throws<InvalidDataException>(() =>
            WorkerDeploymentProgressParser.ParseJsonLines(
                line.Replace("\"target\":\"telebet-29\"", "\"target\":\"attacker\""),
                qa,
                "deploy",
                Digest));
        Assert.Throws<InvalidDataException>(() =>
            WorkerDeploymentProgressParser.ParseJsonLines(
                line.Replace(
                    "\"artifactSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",",
                    string.Empty),
                qa,
                "deploy",
                Digest));
    }

    [Fact]
    public async Task FileAuditStore_IsAppendOnlyAndSuppressesExactDuplicates()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"angel-eye-audit-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "audit.jsonl");
        var store = new FileWorkerDeploymentAuditStore(path);
        WorkerDeploymentAuditEntry entry = SuccessfulQaEntry(Digest);

        Assert.True(await store.AppendAsync(entry, CancellationToken.None));
        Assert.False(await store.AppendAsync(entry, CancellationToken.None));

        IReadOnlyList<WorkerDeploymentAuditEntry> stored =
            await store.ReadAllAsync(CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal(entry, stored[0]);
    }

    [Fact]
    public void TargetConfiguration_OnlyOverlaysKnownFingerprints()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"angel-eye-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "deployment-targets.json");
        File.WriteAllText(
            path,
            """
            {
              "fingerprints": {
                "qa-29": "SHA256:verified-qa",
                "production-30": "SHA256:verified-production",
                "standby-31": "SHA256:verified-standby"
              }
            }
            """);

        IReadOnlyList<WorkerDeploymentTarget> configured =
            WorkerDeploymentTargetConfiguration.Load(path);

        Assert.Equal(
            WorkerDeploymentTargets.All.Select(target => target.Host),
            configured.Select(target => target.Host));
        Assert.All(configured, target => Assert.True(target.HasPinnedFingerprint));

        File.WriteAllText(
            path,
            """
            {
              "fingerprints": {
                "attacker-host": "SHA256:attacker"
              }
            }
            """);
        Assert.Throws<InvalidDataException>(() =>
            WorkerDeploymentTargetConfiguration.Load(path));
    }

    [Fact]
    public async Task Coordinator_DisconnectAfterInstall_RecoversWithoutDuplicateInstall()
    {
        WorkerDeploymentTarget qa = WorkerDeploymentTargets.Get("qa-29");
        var audit = new InMemoryAuditStore();
        var session = new DisconnectingFakeSession(qa, Digest);
        var coordinator = new WorkerDeploymentCoordinator(
            new SingleFakeSessionFactory(session),
            audit);
        var authentication = new WorkerDeploymentAuthentication(
            "deploy",
            @"C:\keys\deploy");
        var release = new WorkerReleaseBundle(
            new WorkerReleaseManifest(
                1,
                "1.4.0",
                "a14aed3",
                "linux-x64",
                "release.tar.gz",
                Digest,
                DateTimeOffset.Parse("2026-07-23T06:00:00Z")),
            "release.tar.gz",
            "release-manifest.json",
            "release-manifest.p7s");

        WorkerDeploymentPlan plan = await coordinator.PreflightAsync(
            qa,
            authentication,
            release,
            "deploy",
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.InstallAsync(
                plan,
                authentication,
                CancellationToken.None));
        Assert.Equal(1, session.InstallCalls);

        WorkerDeploymentOperationResult recovered =
            await coordinator.RecoverAsync(
                qa,
                authentication,
                plan.DeploymentId,
                CancellationToken.None);

        Assert.True(recovered.RemoteResultKnown);
        Assert.Equal(plan.DeploymentId.Value, recovered.Status.LastDeploymentId);
        Assert.Equal(1, session.InstallCalls);
        Assert.Contains(recovered.Progress, entry =>
            entry.Stage == "install-command" && entry.Status == "unknown");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.InstallAsync(
                plan,
                authentication,
                CancellationToken.None));
        Assert.Equal(1, session.InstallCalls);
    }

    private static WorkerDeploymentStatus StatusFor(
        WorkerDeploymentTarget target,
        bool standbySafe) =>
        new(
            target.ExpectedInstanceName,
            target.ExpectedEnvironment,
            target.ExpectedRole,
            "1.3.0",
            "1.2.0",
            target.IsStandby ? !standbySafe : true,
            target.IsStandby ? !standbySafe : true,
            !target.IsStandby,
            "idle",
            null,
            1);

    private static WorkerDeploymentAuditEntry SuccessfulQaEntry(string digest) =>
        new(
            Guid.Parse("4f97045b-ec5d-42c8-8c82-b45c97a1fa92"),
            "qa-29",
            "10.5.32.29",
            "deploy",
            digest,
            "1.3.0",
            "1.4.0",
            "health",
            "succeeded",
            "Worker release is healthy",
            DateTimeOffset.Parse("2026-07-23T06:00:00Z"),
            false,
            null,
            0);

    private sealed class InMemoryAuditStore : IWorkerDeploymentAuditStore
    {
        private readonly List<WorkerDeploymentAuditEntry> _entries = [];

        public Task<bool> AppendAsync(
            WorkerDeploymentAuditEntry entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool duplicate = _entries.Contains(entry);
            if (!duplicate)
            {
                _entries.Add(entry);
            }
            return Task.FromResult(!duplicate);
        }

        public Task<IReadOnlyList<WorkerDeploymentAuditEntry>> ReadAllAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkerDeploymentAuditEntry>>(
                _entries.ToArray());
        }
    }

    private sealed class SingleFakeSessionFactory(
        DisconnectingFakeSession session) : IWorkerDeploymentSessionFactory
    {
        public Task<IWorkerDeploymentSession> ConnectAsync(
            WorkerDeploymentTarget target,
            WorkerDeploymentAuthentication authentication,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(session.Target, target);
            return Task.FromResult<IWorkerDeploymentSession>(session);
        }
    }

    private sealed class DisconnectingFakeSession(
        WorkerDeploymentTarget target,
        string digest) : IWorkerDeploymentSession
    {
        private Guid? _lastDeploymentId;

        public WorkerDeploymentTarget Target { get; } = target;
        public int InstallCalls { get; private set; }

        public Task<WorkerDeploymentCommandResult> GetStatusAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string lastDeployment =
                _lastDeploymentId is null
                    ? "null"
                    : $"\"{_lastDeploymentId.Value:D}\"";
            string lastResult =
                _lastDeploymentId is null ? "null" : "\"succeeded\"";
            return Task.FromResult(new WorkerDeploymentCommandResult(
                0,
                $$"""
                {"instanceName":"{{Target.ExpectedInstanceName}}","environment":"{{Target.ExpectedEnvironment}}","role":"{{Target.ExpectedRole}}","currentVersion":"1.4.0","previousVersion":"1.3.0","serviceActive":true,"serviceEnabled":true,"healthy":true,"stage":"health","lastDeploymentId":{{lastDeployment}},"scriptVersion":1,"lastResult":{{lastResult}},"lastArtifactSha256":"{{digest}}"}
                """,
                string.Empty));
        }

        public Task UploadReleaseAsync(
            DeploymentId deploymentId,
            string artifactPath,
            string manifestPath,
            string signaturePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkerDeploymentCommandResult> PreflightAsync(
            DeploymentId deploymentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkerDeploymentCommandResult(
                0,
                Progress(
                    deploymentId,
                    "preflight",
                    "succeeded",
                    "release verified"),
                string.Empty));
        }

        public Task<WorkerDeploymentCommandResult> InstallAsync(
            DeploymentId deploymentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallCalls++;
            _lastDeploymentId = deploymentId.Value;
            throw new IOException("simulated SSH disconnect");
        }

        public Task<WorkerDeploymentCommandResult> RollbackAsync(
            DeploymentId deploymentId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string Progress(
            DeploymentId deploymentId,
            string stage,
            string status,
            string message) =>
            $$"""
            {"deploymentId":"{{deploymentId}}","target":"{{Target.ExpectedInstanceName}}","operator":"deploy","artifactSha256":"{{digest}}","stage":"{{stage}}","status":"{{status}}","message":"{{message}}","timestampUtc":"2026-07-23T06:00:00Z","previousVersion":"1.3.0","targetVersion":"1.4.0","rollbackPerformed":false,"manualRecoveryPath":null}
            """;
    }
}
