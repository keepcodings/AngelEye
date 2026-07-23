namespace AngelEyeBmsBridge;

public enum ApplicationMode
{
    Query,
    Engineering,
    Deployment
}

/// <summary>Selects the safe default query surface or an explicit engineering surface.</summary>
public static class ApplicationModeSelector
{
    public static ApplicationMode Select(IEnumerable<string> args)
    {
        string[] values = args.ToArray();
        if (values.Any(arg =>
            string.Equals(arg, "--deployment", StringComparison.OrdinalIgnoreCase)))
        {
            return ApplicationMode.Deployment;
        }

        return values.Any(arg =>
            string.Equals(arg, "--engineering", StringComparison.OrdinalIgnoreCase))
            ? ApplicationMode.Engineering
            : ApplicationMode.Query;
    }

    public static bool IsEngineering(IEnumerable<string> args) =>
        Select(args) == ApplicationMode.Engineering;
}
