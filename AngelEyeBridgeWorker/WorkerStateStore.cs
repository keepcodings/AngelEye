using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>
/// Persists runtime shoe progress outside appsettings so service restarts do not reset the current round.
/// </summary>
public sealed class WorkerStateStore
{
    private const int CurrentStateVersion = 2;
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkerShoeState> _states;
    private readonly string? _loadFailure;

    public WorkerStateStore(string path)
    {
        Path = path;
        (_states, _loadFailure) = Load(path);
    }

    public string Path { get; }

    public void Apply(ShoeEndpointSettings settings)
    {
        string key = BuildKey(settings.SourceDataCode, settings.ShoeId);
        lock (_gate)
        {
            if (_loadFailure != null ||
                !_states.TryGetValue(key, out WorkerShoeState? state) ||
                !TryValidate(state, out _))
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
                settings.CurrentRoundId = state.CurrentRoundId;
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
                StateVersion = CurrentStateVersion,
                DeskName = endpoint.DeskName,
                SourceDataCode = endpoint.SourceDataCode,
                ShoeId = endpoint.ShoeId,
                CurrentShoe = endpoint.CurrentShoe,
                CurrentRound = endpoint.CurrentRound,
                CurrentRoundId = endpoint.CurrentRoundId,
                Runtime = endpoint.CaptureRuntimeState(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Persist();
        }
    }

    public void Apply(ShoeEndpoint endpoint)
    {
        string key = BuildKey(endpoint.SourceDataCode, endpoint.ShoeId);
        lock (_gate)
        {
            if (_loadFailure != null)
            {
                endpoint.MarkAlignmentRequired($"Durable state is unavailable: {_loadFailure}");
                return;
            }

            if (!_states.TryGetValue(key, out WorkerShoeState? state))
            {
                endpoint.MarkAlignmentRequired("Durable state is missing for this endpoint.");
                return;
            }

            if (!TryValidate(state, out string error))
            {
                endpoint.MarkAlignmentRequired($"Durable state is invalid: {error}");
                return;
            }

            endpoint.RestoreRuntimeState(state.Runtime!);
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

    private static (Dictionary<string, WorkerShoeState> States, string? Failure) Load(string path)
    {
        if (!File.Exists(path))
        {
            return (
                new Dictionary<string, WorkerShoeState>(StringComparer.OrdinalIgnoreCase),
                "state file is missing");
        }

        try
        {
            string json = File.ReadAllText(path);
            List<WorkerShoeState>? states = JsonSerializer.Deserialize<List<WorkerShoeState>>(json, WorkerSettings.JsonOptions);
            if (states == null)
            {
                return (
                    new Dictionary<string, WorkerShoeState>(StringComparer.OrdinalIgnoreCase),
                    "state file did not contain a state collection");
            }

            Dictionary<string, WorkerShoeState> loaded = states
                .Where(static s => !string.IsNullOrWhiteSpace(s.SourceDataCode) && !string.IsNullOrWhiteSpace(s.ShoeId))
                .ToDictionary(static s => BuildKey(s.SourceDataCode, s.ShoeId), StringComparer.OrdinalIgnoreCase);
            return (loaded, null);
        }
        catch (Exception ex)
        {
            return (
                new Dictionary<string, WorkerShoeState>(StringComparer.OrdinalIgnoreCase),
                $"state file could not be read ({ex.GetType().Name})");
        }
    }

    private static bool TryValidate(WorkerShoeState state, out string error)
    {
        if (state.StateVersion != CurrentStateVersion)
        {
            error = $"unsupported state version {state.StateVersion}";
            return false;
        }

        if (state.CurrentShoe <= 0 || state.CurrentRound < 0)
        {
            error = "shoe or round is outside the valid range";
            return false;
        }

        if (state.CurrentRound == 0 && state.CurrentRoundId.HasValue ||
            state.CurrentRound > 0 && state.CurrentRoundId != state.CurrentRound)
        {
            error = "roundId does not match the durable round identity";
            return false;
        }

        if (state.Runtime is not { } runtime)
        {
            error = "runtime state is missing";
            return false;
        }

        string phase = BridgeRoundPhases.Normalize(runtime.RoundPhase);
        if (!string.Equals(phase, runtime.RoundPhase, StringComparison.Ordinal))
        {
            error = "round phase is unknown";
            return false;
        }

        if (runtime.ShoeEnding &&
            phase is not (
                BridgeRoundPhases.Countdown or
                BridgeRoundPhases.Dealing or
                BridgeRoundPhases.ShoeChangePending))
        {
            error = "shoe-ending state conflicts with round phase";
            return false;
        }

        bool hasNewShoeAction = !string.IsNullOrWhiteSpace(runtime.LastNewShoeActionId);
        bool hasNewShoeReason = !string.IsNullOrWhiteSpace(runtime.LastNewShoeReason);
        if (hasNewShoeAction != hasNewShoeReason ||
            hasNewShoeAction != runtime.LastNewShoeConfirmedAtUtc.HasValue)
        {
            error = "new-shoe audit fields are incomplete";
            return false;
        }

        if (runtime.AwaitingFirstAuthoritativeResultAfterShoeChange &&
            (!hasNewShoeAction || runtime.ShoeEnding))
        {
            error = "new-shoe result quarantine is missing a completed shoe-change audit";
            return false;
        }

        if (phase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing)
        {
            if (state.CurrentRound <= 0 ||
                !runtime.StartGameEventUid.HasValue ||
                runtime.StartGameEventUid.Value == Guid.Empty ||
                runtime.BoundaryObservedAtUtc == null ||
                BridgeBoundaryStrategies.Normalize(runtime.BoundaryStrategy) ==
                    BridgeBoundaryStrategies.DisabledUntilValidated ||
                runtime.StartGameDeliveryState is not ("Prepared" or "Pending" or "LocalOnly"))
            {
                error = "armed round is missing boundary or StartGame identity";
                return false;
            }
        }

        if (!TryValidateCards(runtime.PlayerCards, isPlayer: true) ||
            !TryValidateCards(runtime.BankerCards, isPlayer: false))
        {
            error = "persisted cards are malformed or contradictory";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateCards(IReadOnlyList<BaccaratCard>? cards, bool isPlayer)
    {
        if (cards == null || cards.Count > 3)
        {
            return false;
        }

        HashSet<int> indexes = [];
        foreach (BaccaratCard card in cards)
        {
            if (card.Index is < 1 or > 3 ||
                card.IsPlayer != isPlayer ||
                !indexes.Add(card.Index) ||
                card.Suit is not ("Diamond" or "Club" or "Spade" or "Heart") ||
                card.Value is not ("A" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9" or "10" or "J" or "Q" or "K"))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildKey(string sourceDataCode, string shoeId) => $"{sourceDataCode.Trim()}:{shoeId.Trim()}";
}

public sealed record WorkerShoeState
{
    public int StateVersion { get; init; }

    public string DeskName { get; init; } = string.Empty;

    public string SourceDataCode { get; init; } = string.Empty;

    public string ShoeId { get; init; } = string.Empty;

    public long CurrentShoe { get; init; }

    public long CurrentRound { get; init; }

    public long? CurrentRoundId { get; init; }

    public ShoeRuntimeState? Runtime { get; init; } = new();

    public DateTimeOffset UpdatedAt { get; init; }
}
