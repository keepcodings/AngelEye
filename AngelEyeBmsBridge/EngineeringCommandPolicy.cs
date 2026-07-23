namespace AngelEyeBmsBridge;

/// <summary>Central safety gate for commands that can write to a physical shoe.</summary>
public static class EngineeringCommandPolicy
{
    /// <summary>Allows mock commands, but requires explicit authorization for physical endpoints.</summary>
    public static bool CanSend(bool mockMode, bool allowPhysicalShoeCommands) => mockMode || allowPhysicalShoeCommands;
}
