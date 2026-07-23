using System.Globalization;

namespace AngelEyeBmsBridge;

internal sealed class RecoverRoundBackoffTracker
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    private readonly Dictionary<string, RecoverRoundBackoffState> _states = new(StringComparer.OrdinalIgnoreCase);

    public RecoverRoundBackoffDecision GetDecision(AngelBridgeCommand command, DateTimeOffset nowUtc)
    {
        string key = BuildKey(command);
        if (_states.TryGetValue(key, out RecoverRoundBackoffState? state)
            && state.NextAttemptAtUtc > nowUtc)
        {
            return RecoverRoundBackoffDecision.Deferred(
                key,
                state.FailureCount,
                state.NextAttemptAtUtc - nowUtc);
        }

        return RecoverRoundBackoffDecision.Attempt(key);
    }

    public RecoverRoundBackoffDecision RecordNotFound(AngelBridgeCommand command, DateTimeOffset nowUtc)
    {
        string key = BuildKey(command);
        int failureCount = _states.TryGetValue(key, out RecoverRoundBackoffState? state)
            ? state.FailureCount + 1
            : 1;

        TimeSpan delay = RetryDelays[Math.Min(failureCount - 1, RetryDelays.Length - 1)];
        _states[key] = new RecoverRoundBackoffState(failureCount, nowUtc + delay);
        return RecoverRoundBackoffDecision.RetryLater(key, failureCount, delay);
    }

    public void Clear(AngelBridgeCommand command)
    {
        _states.Remove(BuildKey(command));
    }

    private static string BuildKey(AngelBridgeCommand command)
    {
        return string.Join(
            "|",
            command.SourceDataCode.Trim(),
            command.DeviceId.Trim(),
            command.Shoe?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            command.Round?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            command.RoundId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private sealed record RecoverRoundBackoffState(int FailureCount, DateTimeOffset NextAttemptAtUtc);
}

internal sealed record RecoverRoundBackoffDecision(
    bool ShouldAttempt,
    string Key,
    int FailureCount,
    TimeSpan Delay)
{
    public static RecoverRoundBackoffDecision Attempt(string key) => new(true, key, 0, TimeSpan.Zero);

    public static RecoverRoundBackoffDecision Deferred(string key, int failureCount, TimeSpan remainingDelay) =>
        new(false, key, failureCount, remainingDelay);

    public static RecoverRoundBackoffDecision RetryLater(string key, int failureCount, TimeSpan retryDelay) =>
        new(false, key, failureCount, retryDelay);
}
