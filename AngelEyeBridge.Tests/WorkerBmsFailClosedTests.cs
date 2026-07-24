using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerBmsFailClosedTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AllBmsDisabled_KeepsHealthAndMoxaParsingAvailable_WithoutOutboxWrites()
    {
        int healthPort = GetAvailableTcpPort();
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: false));

        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task running = worker.RunAsync(cancellation.Token);

        try
        {
            JsonDocument health = await WaitForHealthAsync(healthPort, cancellation.Token);
            Assert.False(health.RootElement.GetProperty("bmsDispatcher").GetBoolean());
            Assert.False(worker.IsBmsDispatcherRunning);

            worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));
            Assert.True(worker.Endpoints[0].ShoeEnding);

            await Task.Delay(100, cancellation.Token);
            Assert.Equal(0, CountRows(settings.Bridge.DatabasePath, "bridge_events"));
        }
        finally
        {
            cancellation.Cancel();
            await running;
        }
    }

    [Fact]
    public async Task AuthorizedEndpoint_StartsDispatcher()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true));

        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task running = worker.RunAsync(cancellation.Token);

        try
        {
            await WaitUntilAsync(() => worker.IsBmsDispatcherRunning, cancellation.Token);
            Assert.True(worker.IsBmsDispatcherRunning);
        }
        finally
        {
            cancellation.Cancel();
            await running;
        }
    }

    [Fact]
    public async Task DispatchPolicy_IsFailClosed_ForDisabledAndUnknownEndpoints()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: false,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true),
            Endpoint("902", "SHOE902", bmsTransmitEnabled: false),
            Endpoint("903", "SHOE903", bmsTransmitEnabled: true, enabled: false));

        await using AngelBridgeWorker worker = new(settings);

        Assert.True(worker.IsEventDispatchEnabled(Pending(1, "901", "SHOE901")));
        Assert.False(worker.IsEventDispatchEnabled(Pending(2, "902", "SHOE902")));
        Assert.False(worker.IsEventDispatchEnabled(Pending(3, "903", "SHOE903")));
        Assert.False(worker.IsEventDispatchEnabled(Pending(4, "UNKNOWN", "UNKNOWN")));
    }

    [Fact]
    public async Task ResendEventById_UsesStoredIdentity_AndCannotBypassEndpointSwitch()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: false),
            Endpoint("902", "SHOE902", bmsTransmitEnabled: true));

        await using AngelBridgeWorker worker = new(settings);
        long eventId = await AppendSentEventAsync(worker.Journal, "901", "SHOE901");

        BridgeCommandHandlingResult result = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "resend-disabled-event",
            Type = "ResendEvent",
            EventId = eventId,
            SourceDataCode = "902",
            DeviceId = "SHOE902"
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("Sent", ReadEventStatus(settings.Bridge.DatabasePath, eventId));
        Assert.Equal(1, CountRows(settings.Bridge.DatabasePath, "bridge_recovery_requests"));
    }

    [Fact]
    public async Task ReadOnly_DoesNotBlockAuthorizedLocalOutboxRecovery()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true));

        await using AngelBridgeWorker worker = new(settings);
        long eventId = await AppendSentEventAsync(worker.Journal, "901", "SHOE901");

        BridgeCommandHandlingResult result = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "resend-read-only-event",
            Type = "ResendEvent",
            EventId = eventId
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Handled", result.Status);
        Assert.Equal("Pending", ReadEventStatus(settings.Bridge.DatabasePath, eventId));
    }

    [Fact]
    public async Task ResendEventWithoutDeskIdentity_IsRejectedWithoutSelectingFirstEndpoint()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: false,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true),
            Endpoint("902", "SHOE902", bmsTransmitEnabled: true));

        await using AngelBridgeWorker worker = new(settings);
        long eventId = await AppendSentEventAsync(worker.Journal, "901", "SHOE901");

        BridgeCommandHandlingResult result = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "resend-missing-desk",
            Type = "ResendEvent",
            EventType = "CutCard",
            Shoe = 202607240001,
            Round = 1
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("NotFound", result.Status);
        Assert.Equal("Sent", ReadEventStatus(settings.Bridge.DatabasePath, eventId));
        Assert.Equal(1, CountRows(settings.Bridge.DatabasePath, "bridge_recovery_requests"));
    }

    [Fact]
    public async Task RecoveryCommands_RejectMissingAndAmbiguousEndpointSelectors_WithAudit()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: false,
            healthPort: null,
            Endpoint("901", "SHOE901-A", bmsTransmitEnabled: true),
            Endpoint("901", "SHOE901-B", bmsTransmitEnabled: true));

        await using AngelBridgeWorker worker = new(settings);
        BridgeCommandHandlingResult missing = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "recover-missing-desk",
            Type = "RecoverRound",
            Shoe = 202607240001,
            Round = 1
        }, CancellationToken.None);
        BridgeCommandHandlingResult ambiguous = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "recover-ambiguous-desk",
            Type = "RecoverRound",
            SourceDataCode = "901",
            Shoe = 202607240001,
            Round = 1
        }, CancellationToken.None);

        Assert.Equal("NotFound", missing.Status);
        Assert.Equal("NotFound", ambiguous.Status);
        Assert.Equal(2, CountRows(settings.Bridge.DatabasePath, "bridge_recovery_requests"));
    }

    [Fact]
    public async Task ResendEventById_RejectsUnknownAndMissingStoredIdentity()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: false,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true));

        await using AngelBridgeWorker worker = new(settings);
        long unknownEventId = await AppendSentEventAsync(worker.Journal, "UNKNOWN", "UNKNOWN");

        BridgeCommandHandlingResult unknown = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "resend-unknown-event",
            Type = "ResendEvent",
            EventId = unknownEventId
        }, CancellationToken.None);
        BridgeCommandHandlingResult missing = await worker.HandleBmsCommandAsync(new AngelBridgeCommand
        {
            CommandId = "resend-missing-event",
            Type = "ResendEvent",
            EventId = unknownEventId + 999
        }, CancellationToken.None);

        Assert.Equal("NotFound", unknown.Status);
        Assert.Equal("NotFound", missing.Status);
        Assert.Equal("Sent", ReadEventStatus(settings.Bridge.DatabasePath, unknownEventId));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private WorkerSettings CreateSettings(
        bool readOnly,
        int? healthPort,
        params ShoeEndpointSettings[] endpoints)
    {
        return new WorkerSettings
        {
            Bms = new BmsWorkerSettings
            {
                EventApiUrl = "http://127.0.0.1:1/api/source/angel/events",
                AutoGenerateJwt = false,
                Token = "test-token"
            },
            Bridge = new BridgeWorkerSettings
            {
                InstanceName = "worker-test",
                EnvironmentName = "Test",
                Role = "Worker",
                BridgeId = "worker-test",
                BridgeName = "Worker test",
                DatabasePath = Path.Combine(_directory, "bridge-events.sqlite"),
                StatePath = Path.Combine(_directory, "bridge-state.json"),
                AutoConnect = false,
                ReadOnly = readOnly,
                ReconnectSeconds = 3,
                StatusLogSeconds = 10
            },
            Health = new HealthWorkerSettings
            {
                Enabled = healthPort.HasValue,
                Host = "127.0.0.1",
                Port = healthPort ?? 18080
            },
            Shoes = endpoints.ToList()
        };
    }

    private static ShoeEndpointSettings Endpoint(
        string sourceDataCode,
        string shoeId,
        bool bmsTransmitEnabled,
        bool enabled = true) => new()
    {
        Enabled = enabled,
        BmsTransmitEnabled = bmsTransmitEnabled,
        DeskName = $"{sourceDataCode}桌",
        SourceDataCode = sourceDataCode,
        ShoeId = shoeId,
        CurrentShoe = 202607240001,
        CurrentRound = 1,
        CurrentRoundId = 1,
        ConnectionMode = ShoeConnectionMode.MoxaTcp,
        MoxaHost = "127.0.0.1",
        MoxaPort = 4001
    };

    private static BridgePendingEvent Pending(long eventId, string sourceDataCode, string deviceId) =>
        new(eventId, "CutCard", sourceDataCode, deviceId, 202607240001, 1, "{}", 0);

    private static async Task<long> AppendSentEventAsync(
        BridgeEventJournal journal,
        string sourceDataCode,
        string deviceId)
    {
        long eventId = await journal.AppendAsync(new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["type"] = "CutCard",
            ["source"] = "AngelEye",
            ["sourceDataCode"] = sourceDataCode,
            ["deviceId"] = deviceId,
            ["shoe"] = 202607240001,
            ["round"] = 1,
            ["data"] = new Dictionary<string, object?>()
        });
        await journal.MarkSentAsync(eventId, DateTime.UtcNow, 200);
        return eventId;
    }

    private static string ReadEventStatus(string dbPath, long eventId)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM bridge_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static int CountRows(string dbPath, string table)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int GetAvailableTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<JsonDocument> WaitForHealthAsync(int port, CancellationToken cancellationToken)
    {
        using HttpClient client = new();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response =
                    await client.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(body);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private static byte[] BuildActiveReport(char sequence, params byte[] data)
    {
        byte[] packetWithoutBcc = new byte[data.Length + 3];
        packetWithoutBcc[0] = 0x05;
        packetWithoutBcc[1] = (byte)sequence;
        data.CopyTo(packetWithoutBcc, 2);
        packetWithoutBcc[^1] = 0x03;

        byte[] bcc = Encoding.ASCII.GetBytes(SerialListener.CalculateBcc(packetWithoutBcc));
        return packetWithoutBcc.Concat(bcc).ToArray();
    }
}
