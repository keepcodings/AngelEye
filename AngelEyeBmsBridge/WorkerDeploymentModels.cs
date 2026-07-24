using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelEyeBmsBridge;

public sealed record WorkerDeploymentTarget(
    string Id,
    string DisplayName,
    string Host,
    int SshPort,
    string ExpectedFingerprint,
    string ExpectedInstanceName,
    string ExpectedEnvironment,
    string ExpectedRole,
    string ServiceName,
    bool IsStandby)
{
    [JsonIgnore]
    public bool HasPinnedFingerprint =>
        ExpectedFingerprint.StartsWith("SHA256:", StringComparison.Ordinal) &&
        ExpectedFingerprint.Length > "SHA256:".Length;
}

public static class WorkerDeploymentTargets
{
    public static readonly IReadOnlyList<WorkerDeploymentTarget> All =
        new ReadOnlyCollection<WorkerDeploymentTarget>(
        [
            new(
                "qa-29",
                "QA .29",
                "10.5.32.29",
                53229,
                string.Empty,
                "telebet-29",
                "QA",
                "Primary",
                "angel-eye-bridge",
                false),
            new(
                "production-30",
                "正式主用 .30",
                "10.5.32.30",
                22,
                string.Empty,
                "telebet-30",
                "Production",
                "Primary",
                "angel-eye-bridge",
                false),
            new(
                "standby-31",
                "正式備援 .31",
                "10.5.32.31",
                22,
                string.Empty,
                "telebet-31",
                "Production",
                "Standby",
                "angel-eye-bridge",
                true)
        ]);

    public static WorkerDeploymentTarget Get(string id) =>
        All.FirstOrDefault(target =>
            string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown deployment target.");
}

public static class WorkerDeploymentTargetConfiguration
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AngelEye",
            "deployment-targets.json");

    public static IReadOnlyList<WorkerDeploymentTarget> Load(string path)
    {
        if (!File.Exists(path))
        {
            return WorkerDeploymentTargets.All;
        }

        DeploymentTargetSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<DeploymentTargetSettings>(
                File.ReadAllText(path),
                Options)
                ?? throw new InvalidDataException(
                    "Deployment target configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Deployment target configuration JSON is invalid.",
                exception);
        }
        if (settings.Fingerprints is null)
        {
            throw new InvalidDataException(
                "Deployment target configuration fingerprints are required.");
        }

        var known = WorkerDeploymentTargets.All.ToDictionary(
            target => target.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string fingerprint) in settings.Fingerprints)
        {
            if (!known.ContainsKey(id))
            {
                throw new InvalidDataException(
                    $"Deployment target configuration contains unknown target '{id}'.");
            }
            if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ||
                fingerprint.Length <= "SHA256:".Length)
            {
                throw new InvalidDataException(
                    $"Deployment target '{id}' fingerprint is invalid.");
            }
        }

        return WorkerDeploymentTargets.All
            .Select(target =>
                settings.Fingerprints.TryGetValue(
                    target.Id,
                    out string? fingerprint)
                    ? target with { ExpectedFingerprint = fingerprint }
                    : target)
            .ToArray();
    }

    private sealed record DeploymentTargetSettings(
        IReadOnlyDictionary<string, string> Fingerprints);
}

public sealed record WorkerDeploymentStatus(
    string InstanceName,
    string Environment,
    string Role,
    string CurrentVersion,
    string PreviousVersion,
    bool ServiceActive,
    bool ServiceEnabled,
    bool Healthy,
    string Stage,
    Guid? LastDeploymentId,
    int ScriptVersion = 0,
    string? LastResult = null,
    string? LastArtifactSha256 = null)
{
    public bool Matches(WorkerDeploymentTarget target) =>
        string.Equals(InstanceName, target.ExpectedInstanceName, StringComparison.Ordinal) &&
        string.Equals(Environment, target.ExpectedEnvironment, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Role, target.ExpectedRole, StringComparison.OrdinalIgnoreCase) &&
        (!target.IsStandby || (!ServiceActive && !ServiceEnabled));
}

public static class WorkerDeploymentStatusParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WorkerDeploymentStatus Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkerDeploymentStatus>(json, Options)
                ?? throw new InvalidDataException("Worker deployment status is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Worker deployment status JSON is invalid.",
                exception);
        }
    }
}
