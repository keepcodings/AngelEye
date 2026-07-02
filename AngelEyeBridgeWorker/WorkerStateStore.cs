using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>
/// Persists runtime shoe progress outside appsettings so service restarts do not reset the current round.
/// </summary>
public sealed class WorkerStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkerShoeState> _states;

    public WorkerStateStore(string path)
    {
        Path = path;
        _states = Load(path);
    }

    public string Path { get; }

    public void Apply(ShoeEndpointSettings settings)
    {
        string key = BuildKey(settings.SourceDataCode, settings.ShoeId);
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out WorkerShoeState? state))
            {
                return;
            }

            if (state.CurrentShoe > 0)
            {
                settings.CurrentShoe = state.CurrentShoe;
            }

            if (state.CurrentRound >= 0)
            {
                settings.CurrentRound = state.CurrentRound;
                settings.CurrentRoundId = state.CurrentRound > 0 ? state.CurrentRound : null;
            }
        }
    }

    public void Save(ShoeEndpoint endpoint)
    {
        string key = BuildKey(endpoint.SourceDataCode, endpoint.ShoeId);
        lock (_gate)
        {
            _states[key] = new WorkerShoeState
            {
                DeskName = endpoint.DeskName,
                SourceDataCode = endpoint.SourceDataCode,
                ShoeId = endpoint.ShoeId,
                CurrentShoe = endpoint.CurrentShoe,
                CurrentRound = endpoint.CurrentRound,
                CurrentRoundId = endpoint.CurrentRoundId,
                ShoeEnding = endpoint.ShoeEnding,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Persist();
        }
    }

    private void Persist()
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = Path + ".tmp";
        string json = JsonSerializer.Serialize(_states.Values.OrderBy(static s => s.SourceDataCode).ToList(), WorkerSettings.JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, Path, overwrite: true);
    }

    private static Dictionary<string, WorkerShoeState> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, WorkerShoeState>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            string json = File.ReadAllText(path);
            List<WorkerShoeState>? states = JsonSerializer.Deserialize<List<WorkerShoeState>>(json, WorkerSettings.JsonOptions);
            return states?
                .Where(static s => !string.IsNullOrWhiteSpace(s.SourceDataCode) && !string.IsNullOrWhiteSpace(s.ShoeId))
                .ToDictionary(static s => BuildKey(s.SourceDataCode, s.ShoeId), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, WorkerShoeState>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, WorkerShoeState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string BuildKey(string sourceDataCode, string shoeId) => $"{sourceDataCode.Trim()}:{shoeId.Trim()}";
}

public sealed record WorkerShoeState
{
    public string DeskName { get; init; } = string.Empty;

    public string SourceDataCode { get; init; } = string.Empty;

    public string ShoeId { get; init; } = string.Empty;

    public long CurrentShoe { get; init; }

    public long CurrentRound { get; init; }

    public long? CurrentRoundId { get; init; }

    public bool ShoeEnding { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
