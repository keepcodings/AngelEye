using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>
/// Stores persistent bridge settings in the local SQLite database.
/// </summary>
public sealed class BridgeSettings
{
    // Deployment secrets stay in each machine's local SQLite settings, never in source control.
    private const string DefaultSourceProviderSigningKey = "";
    private const string DefaultBmsUrl = "https://redhood67.infinitybeyonder888test.com/api/source/angel/events";
    private const string DefaultAngelMidoriProviderId = "899e293f-cf47-46b0-bde0-2ed3c7395f17";
    private const string DefaultAngelMidoriTokenSerial = "b1b696a8-70f4-41d6-a549-bc2f4592cf6f";
    private const string SettingsKey = "bridge_settings_json";

    /// <summary>BMS event API URL.</summary>
    public string BmsUrl { get; set; } = DefaultBmsUrl;

    /// <summary>Current JWT bearer token used for BMS API calls.</summary>
    public string BmsToken { get; set; } = string.Empty;

    /// <summary>JWT source provider identifier claim.</summary>
    public string JwtNameIdentifier { get; set; } = DefaultAngelMidoriProviderId;

    /// <summary>JWT source provider token serial number claim.</summary>
    public string JwtSerialNumber { get; set; } = DefaultAngelMidoriTokenSerial;

    /// <summary>JWT issuer expected by BMS.</summary>
    public string JwtIssuer { get; set; } = "gs.com";

    /// <summary>JWT audience expected by BMS.</summary>
    public string JwtAudience { get; set; } = "BMS RESTful API";

    /// <summary>Shared HS256 signing key used to create source provider tokens.</summary>
    public string JwtSigningKey { get; set; } = DefaultSourceProviderSigningKey;

    /// <summary>Generated JWT lifetime in minutes.</summary>
    public int JwtLifetimeMinutes { get; set; } = 10080;

    /// <summary>Default betting countdown seconds for new shoe endpoints.</summary>
    public int TotalBetTimeSeconds { get; set; } = 20;

    /// <summary>Whether the bridge may send OP commands to physical ANGEL shoes.</summary>
    public bool AllowPhysicalShoeCommands { get; set; } = false;

    /// <summary>Physical input mode shared by every configured shoe endpoint.</summary>
    public string ConnectionMode { get; set; } = ShoeConnectionMode.MoxaTcp;

    /// <summary>Configured local shoe endpoints.</summary>
    public List<ShoeEndpointSettings> Shoes { get; set; } = [];

    /// <summary>Path to the local SQLite database used by settings and event outbox tables.</summary>
    public static string DatabasePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge-events.sqlite");

    /// <summary>Legacy JSON settings path used only for one-time migration.</summary>
    public static string SettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge-settings.json");

    /// <summary>
    /// Loads settings from SQLite, falling back to the legacy JSON file and default two-shoe mock layout.
    /// </summary>
    /// <returns>Normalized bridge settings.</returns>
    public static BridgeSettings Load()
    {
        try
        {
            BridgeSettings settings = LoadFromDatabase()
                ?? LoadFromLegacyJson()
                ?? CreateDefault();

            if (settings.Shoes.Count == 0)
            {
                settings.Shoes.AddRange(CreatePit9DefaultShoes());
                settings.Save();
            }
            else if (ShouldUpgradeToPit9MoxaDefaults(settings))
            {
                settings.Shoes.Clear();
                settings.Shoes.AddRange(CreatePit9DefaultShoes());
                settings.ConnectionMode = ShoeConnectionMode.MoxaTcp;
                settings.TotalBetTimeSeconds = 20;
                settings.Save();
            }

            if (ShouldAddPit9QaDefault(settings))
            {
                settings.Shoes.Add(ShoeEndpointSettings.CreatePit9QaMoxaEndpoint());
                settings.Save();
            }

            NormalizeConnectionModeSettings(settings);
            NormalizeShoeMappings(settings);
            NormalizeJwtSettings(settings);
            NormalizeBetTimeSettings(settings);
            NormalizeMockModeSettings(settings);
            return settings;
        }
        catch
        {
            return CreateDefault();
        }
    }

    /// <summary>
    /// Saves the settings JSON into the local SQLite settings table.
    /// </summary>
    public void Save()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        InitializeSettingsDatabase();

        using SqliteConnection connection = CreateSettingsConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bridge_settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", SettingsKey);
        command.Parameters.AddWithValue("$value", json);
        command.ExecuteNonQuery();
    }

    private static BridgeSettings? LoadFromDatabase()
    {
        InitializeSettingsDatabase();

        using SqliteConnection connection = CreateSettingsConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM bridge_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SettingsKey);

        object? value = command.ExecuteScalar();
        if (value is not string json || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<BridgeSettings>(json, JsonOptions);
    }

    private static BridgeSettings? LoadFromLegacyJson()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        string json = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<BridgeSettings>(json, JsonOptions);
    }

    private static void InitializeSettingsDatabase()
    {
        using SqliteConnection connection = CreateSettingsConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS bridge_settings
            (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection CreateSettingsConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return new SqliteConnection(builder.ToString());
    }

    private static BridgeSettings CreateDefault()
    {
        return new BridgeSettings
        {
            ConnectionMode = ShoeConnectionMode.MoxaTcp,
            TotalBetTimeSeconds = 20,
            Shoes = CreatePit9DefaultShoes()
        };
    }

    private static List<ShoeEndpointSettings> CreatePit9DefaultShoes()
    {
        return
        [
            ShoeEndpointSettings.CreatePit9MoxaEndpoint(901, "10.5.32.24"),
            ShoeEndpointSettings.CreatePit9MoxaEndpoint(902, "10.5.32.25"),
            ShoeEndpointSettings.CreatePit9MoxaEndpoint(903, "10.5.32.26"),
            ShoeEndpointSettings.CreatePit9QaMoxaEndpoint()
        ];
    }

    private static bool ShouldAddPit9QaDefault(BridgeSettings settings)
    {
        if (settings.Shoes.Any(IsPit9QaEndpoint))
        {
            return false;
        }

        return HasPit9Table(settings, 901, "10.5.32.24") &&
            HasPit9Table(settings, 902, "10.5.32.25") &&
            HasPit9Table(settings, 903, "10.5.32.26");
    }

    private static bool HasPit9Table(BridgeSettings settings, int tableNumber, string moxaHost)
    {
        string sourceDataCode = tableNumber.ToString();
        string shoeId = $"SHOE{tableNumber}";
        return settings.Shoes.Any(shoe =>
            string.Equals(shoe.SourceDataCode.Trim(), sourceDataCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shoe.ShoeId.Trim(), shoeId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shoe.MoxaHost.Trim(), moxaHost, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPit9QaEndpoint(ShoeEndpointSettings shoe)
    {
        return string.Equals(shoe.SourceDataCode.Trim(), "ANGEL_BACQA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shoe.ShoeId.Trim(), "SHOEQA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shoe.MoxaHost.Trim(), "10.5.32.124", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUpgradeToPit9MoxaDefaults(BridgeSettings settings)
    {
        if (settings.Shoes.Count == 1)
        {
            return IsLegacyGeneratedDefaultShoe(settings.Shoes[0], 1);
        }

        if (settings.Shoes.Count == 2)
        {
            return IsLegacyGeneratedDefaultShoe(settings.Shoes[0], 1) &&
                IsLegacyGeneratedDefaultShoe(settings.Shoes[1], 2);
        }

        return false;
    }

    private static bool IsLegacyGeneratedDefaultShoe(ShoeEndpointSettings shoe, int number)
    {
        ShoeEndpointSettings defaults = ShoeEndpointSettings.CreateMockSimulation(number);
        bool noPhysicalTarget = string.IsNullOrWhiteSpace(shoe.ComPort) && string.IsNullOrWhiteSpace(shoe.MoxaHost);
        bool oldSourceDataId = string.IsNullOrWhiteSpace(shoe.SourceDataId) ||
            string.Equals(shoe.SourceDataId.Trim(), defaults.SourceDataId, StringComparison.OrdinalIgnoreCase);

        return noPhysicalTarget &&
            string.Equals(shoe.SourceDataCode.Trim(), defaults.SourceDataCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(shoe.ShoeId.Trim(), defaults.ShoeId, StringComparison.OrdinalIgnoreCase) &&
            oldSourceDataId;
    }

    private static void NormalizeShoeMappings(BridgeSettings settings)
    {
        for (int i = 0; i < settings.Shoes.Count; i++)
        {
            settings.Shoes[i].ApplyDefaultMapping(i + 1);
        }

        settings.Save();
    }

    private static void NormalizeConnectionModeSettings(BridgeSettings settings)
    {
        string normalizedMode = ShoeConnectionMode.Normalize(settings.ConnectionMode);
        if (string.Equals(normalizedMode, ShoeConnectionMode.ComPort, StringComparison.OrdinalIgnoreCase) &&
            settings.Shoes.Any(shoe => string.Equals(ShoeConnectionMode.Normalize(shoe.ConnectionMode), ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedMode = ShoeConnectionMode.MoxaTcp;
        }

        settings.ConnectionMode = normalizedMode;
        foreach (ShoeEndpointSettings shoe in settings.Shoes)
        {
            shoe.ConnectionMode = normalizedMode;
        }

        settings.Save();
    }

    private static void NormalizeJwtSettings(BridgeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.JwtNameIdentifier))
        {
            settings.JwtNameIdentifier = DefaultAngelMidoriProviderId;
        }

        if (string.IsNullOrWhiteSpace(settings.JwtSerialNumber))
        {
            settings.JwtSerialNumber = DefaultAngelMidoriTokenSerial;
        }

        if (string.IsNullOrWhiteSpace(settings.JwtIssuer))
        {
            settings.JwtIssuer = "gs.com";
        }

        if (string.IsNullOrWhiteSpace(settings.JwtAudience))
        {
            settings.JwtAudience = "BMS RESTful API";
        }

        if (string.IsNullOrWhiteSpace(settings.JwtSigningKey))
        {
            settings.JwtSigningKey = DefaultSourceProviderSigningKey;
        }

        if (settings.JwtLifetimeMinutes <= 0)
        {
            settings.JwtLifetimeMinutes = 10080;
        }

        settings.Save();
    }

    private static void NormalizeBetTimeSettings(BridgeSettings settings)
    {
        if (settings.TotalBetTimeSeconds <= 0)
        {
            settings.TotalBetTimeSeconds = 20;
        }

        settings.TotalBetTimeSeconds = Math.Clamp(settings.TotalBetTimeSeconds, 5, 120);
        foreach (ShoeEndpointSettings shoe in settings.Shoes)
        {
            if (shoe.TotalBetTimeSeconds <= 0)
            {
                shoe.TotalBetTimeSeconds = settings.TotalBetTimeSeconds;
            }

            shoe.TotalBetTimeSeconds = Math.Clamp(shoe.TotalBetTimeSeconds, 5, 120);
        }

        settings.Save();
    }

    private static void NormalizeMockModeSettings(BridgeSettings settings)
    {
        foreach (ShoeEndpointSettings shoe in settings.Shoes)
        {
            shoe.MockMode = false;
        }

        settings.Save();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}

/// <summary>
/// Physical input transport used by an endpoint.
/// </summary>
public static class ShoeConnectionMode
{
    /// <summary>Use a Windows serial COM port.</summary>
    public const string ComPort = "ComPort";

    /// <summary>Connect directly to a MOXA/NPort TCP server endpoint.</summary>
    public const string MoxaTcp = "MoxaTcp";

    /// <summary>Normalizes a persisted connection mode value.</summary>
    public static string Normalize(string? value)
    {
        return string.Equals(value?.Trim(), MoxaTcp, StringComparison.OrdinalIgnoreCase)
            ? MoxaTcp
            : ComPort;
    }
}

/// <summary>
/// Persistent configuration for one physical or mock ANGEL shoe endpoint.
/// </summary>
public sealed class ShoeEndpointSettings
{
    /// <summary>Whether this endpoint is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether events from this endpoint are allowed to be sent to BMS.</summary>
    public bool BmsTransmitEnabled { get; set; } = false;

    /// <summary>Legacy BMS system desk GUID retained for old settings migration; not sent to BMS.</summary>
    public string DeskId { get; set; } = "E483D573-20FD-45A0-BCF0-A1BDF3C15101";

    /// <summary>Human-readable desk name shown in the bridge UI.</summary>
    public string DeskName { get; set; } = "百家樂一號";

    /// <summary>Optional BMS SourceData GUID mapped to this endpoint for diagnostics.</summary>
    public string SourceDataId { get; set; } = "A8B0E2E1-65F4-4D5D-84C7-6CE30B115101";

    /// <summary>Primary BMS source table code mapped to this endpoint.</summary>
    public string SourceDataCode { get; set; } = "ANGEL_BAC01";

    /// <summary>Bridge-side shoe device identifier.</summary>
    public string ShoeId { get; set; } = "SHOE01";

    /// <summary>Current BMS shoe number.</summary>
    public long CurrentShoe { get; set; } = BridgeGameNumbering.TodayFirstShoe();

    /// <summary>Current BMS round number within the shoe.</summary>
    public long CurrentRound { get; set; }

    /// <summary>Current bridge round identifier, when available.</summary>
    public long? CurrentRoundId { get; set; }

    /// <summary>Serial port assigned to this endpoint.</summary>
    public string ComPort { get; set; } = string.Empty;

    /// <summary>Physical input mode: Windows COM port or direct MOXA TCP.</summary>
    public string ConnectionMode { get; set; } = ShoeConnectionMode.ComPort;

    /// <summary>MOXA/NPort IP address or host name when <see cref="ConnectionMode"/> is MOXA TCP.</summary>
    public string MoxaHost { get; set; } = string.Empty;

    /// <summary>MOXA/NPort TCP server port when <see cref="ConnectionMode"/> is MOXA TCP.</summary>
    public int MoxaPort { get; set; } = 4001;

    /// <summary>Whether this endpoint runs without physical serial hardware.</summary>
    public bool MockMode { get; set; } = false;

    /// <summary>Betting countdown seconds for this endpoint.</summary>
    public int TotalBetTimeSeconds { get; set; } = 20;

    /// <summary>
    /// Creates the default endpoint configuration.
    /// </summary>
    /// <returns>A first-table mock endpoint configuration.</returns>
    public static ShoeEndpointSettings CreateDefault()
    {
        return CreateDefaultForNumber(1);
    }

    /// <summary>
    /// Creates the default endpoint for the one-based UI row number.
    /// </summary>
    /// <param name="number">One-based endpoint number.</param>
    /// <returns>A PIT9 endpoint for the first four rows, otherwise a generated local endpoint.</returns>
    public static ShoeEndpointSettings CreateDefaultForNumber(int number)
    {
        return number switch
        {
            1 => CreatePit9MoxaEndpoint(901, "10.5.32.24"),
            2 => CreatePit9MoxaEndpoint(902, "10.5.32.25"),
            3 => CreatePit9MoxaEndpoint(903, "10.5.32.26"),
            4 => CreatePit9QaMoxaEndpoint(),
            _ => CreateMockSimulation(number)
        };
    }

    /// <summary>
    /// Creates a PIT9 MOXA TCP endpoint for the specified table.
    /// </summary>
    /// <param name="tableNumber">PIT9 table number.</param>
    /// <param name="moxaHost">NPort 5110 IP address.</param>
    /// <returns>A physical MOXA TCP endpoint configuration.</returns>
    public static ShoeEndpointSettings CreatePit9MoxaEndpoint(int tableNumber, string moxaHost)
    {
        return tableNumber switch
        {
            901 => CreateMoxaEndpoint("901桌", "901", "SHOE901", moxaHost, "9d1f77f4-4f00-40ce-bda6-8cd6476a2ad4", bmsTransmitEnabled: true),
            902 => CreateMoxaEndpoint("902桌", "902", "SHOE902", moxaHost, "9ed122db-7622-42bf-a4cc-c84a2503a351"),
            903 => CreateMoxaEndpoint("903桌", "903", "SHOE903", moxaHost, "5c3762c6-ea07-44a1-b6ad-b4467b1fdddd"),
            _ => CreateMoxaEndpoint($"{tableNumber}桌", tableNumber.ToString(), $"SHOE{tableNumber}", moxaHost)
        };
    }

    /// <summary>
    /// Creates the PIT9 QA MOXA TCP endpoint.
    /// </summary>
    /// <returns>A QA endpoint configuration.</returns>
    public static ShoeEndpointSettings CreatePit9QaMoxaEndpoint()
    {
        return CreateMoxaEndpoint("QA桌", "ANGEL_BACQA", "SHOEQA", "10.5.32.124");
    }

    private static ShoeEndpointSettings CreateMoxaEndpoint(
        string deskName,
        string sourceDataCode,
        string shoeId,
        string moxaHost,
        string sourceDataId = "",
        bool bmsTransmitEnabled = false)
    {
        return new ShoeEndpointSettings
        {
            Enabled = true,
            BmsTransmitEnabled = bmsTransmitEnabled,
            DeskId = Guid.Empty.ToString(),
            DeskName = deskName,
            SourceDataId = sourceDataId,
            SourceDataCode = sourceDataCode,
            ShoeId = shoeId,
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe(),
            CurrentRound = 1,
            CurrentRoundId = 1,
            ComPort = string.Empty,
            ConnectionMode = ShoeConnectionMode.MoxaTcp,
            MoxaHost = moxaHost,
            MoxaPort = 4001,
            MockMode = false,
            TotalBetTimeSeconds = 20
        };
    }

    /// <summary>
    /// Creates a deterministic mock endpoint for local multi-shoe simulation.
    /// </summary>
    /// <param name="number">One-based shoe/table number.</param>
    /// <returns>A mock endpoint configuration with stable BMS mapping values.</returns>
    public static ShoeEndpointSettings CreateMockSimulation(int number)
    {
        return new ShoeEndpointSettings
        {
            Enabled = true,
            BmsTransmitEnabled = false,
            DeskId = number switch
            {
                1 => "E483D573-20FD-45A0-BCF0-A1BDF3C15101",
                2 => "E483D573-20FD-45A0-BCF0-A1BDF3C15102",
                _ => Guid.Empty.ToString()
            },
            DeskName = number switch
            {
                1 => "百家樂一號",
                2 => "百家樂二號",
                _ => $"百家樂{number}號"
            },
            SourceDataId = number switch
            {
                1 => "A8B0E2E1-65F4-4D5D-84C7-6CE30B115101",
                2 => "A8B0E2E1-65F4-4D5D-84C7-6CE30B115102",
                _ => string.Empty
            },
            SourceDataCode = $"ANGEL_BAC{number:00}",
            ShoeId = $"SHOE{number:00}",
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe(),
            CurrentRound = 1,
            CurrentRoundId = 1,
            ComPort = string.Empty,
            ConnectionMode = ShoeConnectionMode.ComPort,
            MoxaHost = string.Empty,
            MoxaPort = 4001,
            MockMode = false,
            TotalBetTimeSeconds = 20
        };
    }

    /// <summary>
    /// Applies deterministic BMS mapping fields for the endpoint number.
    /// </summary>
    /// <param name="number">One-based shoe/table number.</param>
    public void ApplyDefaultMapping(int number)
    {
        ShoeEndpointSettings defaults = CreateDefaultForNumber(number);
        string oldDeskId = DeskId.Trim();

        if (string.IsNullOrWhiteSpace(DeskName))
        {
            DeskName = Guid.TryParse(oldDeskId, out _) ? defaults.DeskName : oldDeskId;
        }

        if (string.IsNullOrWhiteSpace(DeskName))
        {
            DeskName = defaults.DeskName;
        }

        if (!Guid.TryParse(oldDeskId, out _))
        {
            DeskId = defaults.DeskId;
        }

        if (ShouldApplyDefaultSourceDataId(SourceDataId, defaults.SourceDataId))
        {
            SourceDataId = defaults.SourceDataId;
        }

        if (string.IsNullOrWhiteSpace(SourceDataCode) || IsGeneratedAngelSourceDataCode(SourceDataCode))
        {
            SourceDataCode = defaults.SourceDataCode;
        }

        if (string.IsNullOrWhiteSpace(ShoeId) || IsGeneratedShoeId(ShoeId))
        {
            ShoeId = defaults.ShoeId;
        }

        ConnectionMode = ShoeConnectionMode.Normalize(ConnectionMode);

        if (string.IsNullOrWhiteSpace(MoxaHost) &&
            !string.IsNullOrWhiteSpace(defaults.MoxaHost) &&
            string.Equals(SourceDataCode.Trim(), defaults.SourceDataCode, StringComparison.OrdinalIgnoreCase))
        {
            MoxaHost = defaults.MoxaHost;
        }

        if (MoxaPort <= 0 || MoxaPort > 65535)
        {
            MoxaPort = 4001;
        }
    }

    private static bool ShouldApplyDefaultSourceDataId(string currentValue, string defaultValue)
    {
        string value = currentValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return !string.IsNullOrWhiteSpace(defaultValue);
        }

        if (!Guid.TryParse(value, out Guid sourceDataId) || sourceDataId == Guid.Empty)
        {
            return true;
        }

        return IsGeneratedAngelSourceDataId(value);
    }

    private static bool IsGeneratedAngelSourceDataCode(string value)
    {
        string trimmed = value.Trim();
        const string prefix = "ANGEL_BAC";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string suffix = trimmed[prefix.Length..];
        return suffix.Length == 2 && suffix.All(char.IsDigit);
    }

    private static bool IsGeneratedShoeId(string value)
    {
        string trimmed = value.Trim();
        const string prefix = "SHOE";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string suffix = trimmed[prefix.Length..];
        return suffix.Length == 2 && suffix.All(char.IsDigit);
    }

    private static bool IsGeneratedAngelSourceDataId(string value)
    {
        return string.Equals(value.Trim(), "A8B0E2E1-65F4-4D5D-84C7-6CE30B115101", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "A8B0E2E1-65F4-4D5D-84C7-6CE30B115102", StringComparison.OrdinalIgnoreCase);
    }
}
