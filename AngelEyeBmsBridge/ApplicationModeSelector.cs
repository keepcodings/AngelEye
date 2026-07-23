namespace AngelEyeBmsBridge;

/// <summary>Selects the safe default query surface or the explicit engineering surface.</summary>
public static class ApplicationModeSelector
{
    public static bool IsEngineering(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--engineering", StringComparison.OrdinalIgnoreCase));
}
