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
    public async Task AllBmsDisabled_KeepsHealthAndMoxaParsingAvailable_WithLocalOnlyEvidence()
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

            await WaitUntilAsync(
                () => CountRows(settings.Bridge.DatabasePath, "bridge_events") == 1,
                cancellation.Token);
            Assert.Equal(
                1,
                CountEvents(settings.Bridge.DatabasePath, type: "CutCardDrawn", status: "LocalOnly"));
            BridgeOutboxStatus outbox = await worker.Journal.GetOutboxStatusAsync("901", "SHOE901");
            Assert.Equal(0, outbox.PendingCount);
            Assert.Equal(0, outbox.FailedCount);
        }
        finally
        {
            cancellation.Cancel();
            await running;
        }
    }

    [Fact]
    public async Task ConnectWithLegacyAutoStartFlag_DoesNotCreateStartGameOrChangeRound()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int moxaPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        ShoeEndpointSettings endpoint = Endpoint("901", "SHOE901", bmsTransmitEnabled: false);
        endpoint.MoxaPort = moxaPort;
        WorkerSettings settings = CreateSettings(readOnly: true, healthPort: null, endpoint);
        settings.Bridge.AutoConnect = true;
        settings.Bridge.AutoStartRoundOnConnect = true;
        long initialShoe = endpoint.CurrentShoe;
        long initialRound = endpoint.CurrentRound;

        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task<TcpClient> acceptedConnection = listener.AcceptTcpClientAsync(cancellation.Token).AsTask();
        Task running = worker.RunAsync(cancellation.Token);

        try
        {
            using TcpClient peer = await acceptedConnection;
            await WaitUntilAsync(() => worker.Endpoints[0].IsConnected, cancellation.Token);
            await Task.Delay(100, cancellation.Token);

            Assert.Equal(initialShoe, worker.Endpoints[0].CurrentShoe);
            Assert.Equal(initialRound, worker.Endpoints[0].CurrentRound);
            Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));

            peer.Dispose();
            await WaitUntilAsync(() => !worker.Endpoints[0].IsConnected, cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await running;
            listener.Stop();
        }
    }

    [Fact]
    public async Task FirstCardBeforeStartGame_KeepsCardAndResultLocalOnly()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true));
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        long initialShoe = worker.Endpoints[0].CurrentShoe;
        long initialRound = worker.Endpoints[0].CurrentRound;

        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "CardDrawn") == 1,
            cancellation.Token);
        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('2', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);

        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));
        Assert.Equal(initialShoe, worker.Endpoints[0].CurrentShoe);
        Assert.Equal(initialRound, worker.Endpoints[0].CurrentRound);
        Assert.Equal(
            1,
            CountEvents(settings.Bridge.DatabasePath, type: "CardDrawn", status: "LocalOnly"));
        Assert.Equal(
            1,
            CountEvents(settings.Bridge.DatabasePath, type: "GameResult", status: "LocalOnly"));
        Assert.Empty(await worker.Journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
    }

    [Fact]
    public async Task TransportStatusTelegram_DoesNotClearShoeEnding()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: false));
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));
        await WaitUntilAsync(() => worker.Endpoints[0].ShoeEnding, cancellation.Token);
        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('2', (byte)'S'));

        Assert.True(worker.Endpoints[0].ShoeEnding);
    }

    [Fact]
    public async Task FirstCardDuringResultDelay_CancelsDerivedStartGameBoundary()
    {
        ShoeEndpointSettings endpoint = Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpoint.MockMode = true;
        WorkerSettings settings = CreateSettings(readOnly: true, healthPort: null, endpoint);
        settings.Bridge.AutoStartNextRoundAfterResult = true;
        settings.Bridge.ResultToNextRoundDelaySeconds = 1;
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        worker.Endpoints[0].Connect();

        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('1', (byte)'G', 0x91));
        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1 &&
                  CountEvents(settings.Bridge.DatabasePath, type: "CardDrawn") == 1,
            cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), cancellation.Token);

        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));
        Assert.Empty(await worker.Journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0x70)]
    [InlineData(0x00)]
    public async Task NonNormalTerminalResult_DoesNotCreateDerivedStartGameBoundary(byte resultPayload)
    {
        ShoeEndpointSettings endpoint = Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpoint.MockMode = true;
        WorkerSettings settings = CreateSettings(readOnly: true, healthPort: null, endpoint);
        settings.Bridge.AutoStartNextRoundAfterResult = true;
        settings.Bridge.ResultToNextRoundDelaySeconds = 1;
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        worker.Endpoints[0].Connect();

        worker.Endpoints[0].Listener.InjectBytes(
            BuildActiveReport('1', (byte)'G', resultPayload));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), cancellation.Token);

        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));
        Assert.Empty(await worker.Journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
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
    public async Task LegacyResendCommand_IsRejectedWithoutRequeueingLiveOutbox()
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

        Assert.False(result.Success);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("Sent", ReadEventStatus(settings.Bridge.DatabasePath, eventId));
        Assert.Contains("dedicated /recoveries", result.Message, StringComparison.Ordinal);
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
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("Sent", ReadEventStatus(settings.Bridge.DatabasePath, eventId));
        Assert.Equal(1, CountRows(settings.Bridge.DatabasePath, "bridge_recovery_requests"));
    }

    [Fact]
    public async Task LegacyRecoveryCommands_AreRejectedWithAudit()
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

        Assert.Equal("Rejected", missing.Status);
        Assert.Equal("Rejected", ambiguous.Status);
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

        Assert.Equal("Rejected", unknown.Status);
        Assert.Equal("Rejected", missing.Status);
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
                EventApiUrl = "https://127.0.0.1:1/api/source/angel/events",
                AutoGenerateJwt = false,
                JwtSigningKey = string.Empty,
                ClientId = "worker-test",
                ClientSecret = "test-client-secret"
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
                AutoStartNextRoundAfterResult = false,
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
            ["type"] = "StartGame",
            ["source"] = "AngelEye",
            ["sourceDataCode"] = sourceDataCode,
            ["deviceId"] = deviceId,
            ["shoe"] = 202607240001,
            ["round"] = 1,
            ["data"] = new Dictionary<string, object?>()
        });
        Assert.True(await journal.TryClaimForDeliveryAsync(eventId, DateTime.UtcNow));
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

    private static int CountEvents(string dbPath, string? type = null, string? status = null)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        List<string> clauses = [];
        if (!string.IsNullOrWhiteSpace(type))
        {
            clauses.Add("type = $type");
            command.Parameters.AddWithValue("$type", type);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            clauses.Add("status = $status");
            command.Parameters.AddWithValue("$status", status);
        }
        command.CommandText = "SELECT COUNT(*) FROM bridge_events" +
            (clauses.Count == 0 ? ";" : $" WHERE {string.Join(" AND ", clauses)};");
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
