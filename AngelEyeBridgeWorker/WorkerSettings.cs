using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelEyeBmsBridge;

/// <summary>
/// File-backed configuration for the Linux ANGEL bridge worker.
/// </summary>
public sealed class WorkerSettings
{
    public BmsWorkerSettings Bms { get; set; } = new();

    public BridgeWorkerSettings Bridge { get; set; } = new();

    public HealthWorkerSettings Health { get; set; } = new();

    public List<ShoeEndpointSettings> Shoes { get; set; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkerSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"設定檔不存在: {path}", path);
        }

        string json = File.ReadAllText(path);
        WorkerSettings? settings = JsonSerializer.Deserialize<WorkerSettings>(json, JsonOptions);
        if (settings == null)
        {
            throw new InvalidOperationException($"設定檔無法解析: {path}");
        }

        settings.Normalize(Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory);
        settings.Validate();
        return settings;
    }

    public void Normalize(string configDirectory)
    {
        Bms.Normalize();
        Bridge.Normalize(configDirectory);
        Health.Normalize();

        string mode = ShoeConnectionMode.Normalize(Bridge.ConnectionMode);
        Bridge.ConnectionMode = mode;
        for (int i = 0; i < Shoes.Count; i++)
        {
            ShoeEndpointSettings shoe = Shoes[i];
            int number = i + 1;
            shoe.DeskName = string.IsNullOrWhiteSpace(shoe.DeskName)
                ? $"百家樂{number}號"
                : shoe.DeskName.Trim();
            shoe.SourceDataCode = string.IsNullOrWhiteSpace(shoe.SourceDataCode)
                ? $"ANGEL_BAC{number:00}"
                : shoe.SourceDataCode.Trim();
            shoe.SourceDataId = shoe.SourceDataId.Trim();
            shoe.ShoeId = string.IsNullOrWhiteSpace(shoe.ShoeId)
                ? $"SHOE{number:00}"
                : shoe.ShoeId.Trim();
            shoe.ComPort = shoe.ComPort.Trim();
            shoe.MoxaHost = shoe.MoxaHost.Trim();
            shoe.ConnectionMode = mode;
            shoe.MockMode = false;
            shoe.TotalBetTimeSeconds = Math.Clamp(shoe.TotalBetTimeSeconds <= 0 ? Bridge.TotalBetTimeSeconds : shoe.TotalBetTimeSeconds, 5, 120);
            if (shoe.CurrentShoe <= 0)
            {
                shoe.CurrentShoe = BridgeGameNumbering.TodayFirstShoe();
            }

            if (shoe.CurrentRound < 0)
            {
                shoe.CurrentRound = 0;
            }

            if (shoe.MoxaPort <= 0 || shoe.MoxaPort > 65535)
            {
                shoe.MoxaPort = Bridge.DefaultMoxaPort;
            }
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Bridge.InstanceName))
        {
            throw new InvalidOperationException("Bridge:InstanceName 不可為空。");
        }

        if (string.IsNullOrWhiteSpace(Bridge.EnvironmentName))
        {
            throw new InvalidOperationException("Bridge:Environment 不可為空。");
        }

        if (string.IsNullOrWhiteSpace(Bridge.Role))
        {
            throw new InvalidOperationException("Bridge:Role 不可為空。");
        }

        if (string.IsNullOrWhiteSpace(Bms.EventApiUrl))
        {
            throw new InvalidOperationException("Bms:EventApiUrl 不可為空。");
        }

        if (Shoes.Count == 0)
        {
            throw new InvalidOperationException("Shoes 至少要設定一張桌。");
        }

        HashSet<string> sourceDataCodes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> shoeIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ShoeEndpointSettings shoe in Shoes)
        {
            if (!sourceDataCodes.Add(shoe.SourceDataCode))
            {
                throw new InvalidOperationException($"來源桌碼重複: {shoe.SourceDataCode}");
            }

            if (!shoeIds.Add(shoe.ShoeId))
            {
                throw new InvalidOperationException($"端點 ID / 牌盒 ID 重複: {shoe.ShoeId}");
            }

            if (!shoe.Enabled)
            {
                continue;
            }

            if (string.Equals(Bridge.ConnectionMode, ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(shoe.MoxaHost))
                {
                    throw new InvalidOperationException($"{shoe.DeskName} 未設定 MoxaHost。");
                }

                if (shoe.MoxaPort <= 0 || shoe.MoxaPort > 65535)
                {
                    throw new InvalidOperationException($"{shoe.DeskName} MoxaPort 不合法。");
                }
            }
            else if (string.IsNullOrWhiteSpace(shoe.ComPort))
            {
                throw new InvalidOperationException($"{shoe.DeskName} 未設定 ComPort。");
            }
        }
    }
}

public sealed class BmsWorkerSettings
{
    public string EventApiUrl { get; set; } = "https://redhood67.infinitybeyonder888test.com/api/source/angel/events";

    public string Token { get; set; } = string.Empty;

    public bool AutoGenerateJwt { get; set; } = true;

    public string JwtNameIdentifier { get; set; } = "899e293f-cf47-46b0-bde0-2ed3c7395f17";

    public string JwtSerialNumber { get; set; } = "b1b696a8-70f4-41d6-a549-bc2f4592cf6f";

    public string JwtIssuer { get; set; } = "gs.com";

    public string JwtAudience { get; set; } = "BMS RESTful API";

    public string JwtSigningKey { get; set; } = string.Empty;

    public int JwtLifetimeMinutes { get; set; } = 10080;

    public void Normalize()
    {
        EventApiUrl = EventApiUrl.Trim();
        Token = Token.Trim();
        JwtNameIdentifier = JwtNameIdentifier.Trim();
        JwtSerialNumber = JwtSerialNumber.Trim();
        JwtIssuer = JwtIssuer.Trim();
        JwtAudience = JwtAudience.Trim();
        JwtSigningKey = JwtSigningKey.Trim();
        JwtLifetimeMinutes = Math.Max(JwtLifetimeMinutes, 1);
    }
}

public sealed class BridgeWorkerSettings
{
    public string InstanceName { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string EnvironmentName { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string BridgeId { get; set; } = Environment.MachineName;

    public string BridgeName { get; set; } = "AngelEyeBridge";

    public string ConnectionMode { get; set; } = ShoeConnectionMode.MoxaTcp;

    public string DatabasePath { get; set; } = "bridge-events.sqlite";

    public string StatePath { get; set; } = "bridge-state.json";

    public bool AutoConnect { get; set; } = true;

    public bool AutoStartRoundOnConnect { get; set; } = true;

    public bool AutoStartNextRoundAfterResult { get; set; } = true;

    public int ResultToNextRoundDelaySeconds { get; set; } = 3;

    public int TotalBetTimeSeconds { get; set; } = 20;

    public int ReconnectSeconds { get; set; } = 10;

    public int StatusLogSeconds { get; set; } = 30;

    public int DefaultMoxaPort { get; set; } = 4001;

    public bool ReadOnly { get; set; } = true;

    public void Normalize(string configDirectory)
    {
        InstanceName = InstanceName.Trim();
        EnvironmentName = EnvironmentName.Trim();
        Role = Role.Trim();
        BridgeId = string.IsNullOrWhiteSpace(BridgeId) ? Environment.MachineName : BridgeId.Trim();
        BridgeName = string.IsNullOrWhiteSpace(BridgeName) ? "AngelEyeBridge" : BridgeName.Trim();
        ConnectionMode = ShoeConnectionMode.Normalize(ConnectionMode);
        TotalBetTimeSeconds = Math.Clamp(TotalBetTimeSeconds, 5, 120);
        ResultToNextRoundDelaySeconds = Math.Clamp(ResultToNextRoundDelaySeconds, 0, 60);
        ReconnectSeconds = Math.Clamp(ReconnectSeconds, 3, 300);
        StatusLogSeconds = Math.Clamp(StatusLogSeconds, 10, 600);
        DefaultMoxaPort = Math.Clamp(DefaultMoxaPort, 1, 65535);

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            DatabasePath = "bridge-events.sqlite";
        }

        if (!Path.IsPathRooted(DatabasePath))
        {
            DatabasePath = Path.GetFullPath(Path.Combine(configDirectory, DatabasePath));
        }

        if (string.IsNullOrWhiteSpace(StatePath))
        {
            StatePath = "bridge-state.json";
        }

        if (!Path.IsPathRooted(StatePath))
        {
            StatePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(DatabasePath) ?? configDirectory, StatePath));
        }
    }
}

public sealed class HealthWorkerSettings
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 18080;

    public void Normalize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        Port = Math.Clamp(Port, 1, 65535);
    }
}
