namespace AngelEyeBmsBridge;

/// <summary>
/// Represents one baccarat card currently tracked by the headless bridge.
/// </summary>
public class BaccaratCard
{
    /// <summary>Card suit name.</summary>
    public string Suit { get; set; } = string.Empty;

    /// <summary>Card rank value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Draw index reported by the shoe.</summary>
    public int Index { get; set; }

    /// <summary>True when the card belongs to the player side.</summary>
    public bool IsPlayer { get; set; }
}
