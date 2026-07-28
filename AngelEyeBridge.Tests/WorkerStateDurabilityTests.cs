using System.Text;
using System.Text.Json;
using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerStateDurabilityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "angel-eye-state-tests", Guid.NewGuid().ToString("N"));

    public WorkerStateDurabilityTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void MissingState_FailsClosedIntoAlignmentRequired()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: false);
        WorkerStateStore store = new(settings.Bridge.StatePath);
        ShoeEndpoint endpoint = new(settings.Shoes[0]);

        store.Apply(endpoint);

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Contains("missing", endpoint.AlignmentReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(endpoint.StartGameEventUid);
    }

    [Fact]
    public void CorruptState_FailsClosedWithoutUsingConfiguredRoundAsProof()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: false);
        File.WriteAllText(settings.Bridge.StatePath, "{not-json");
        WorkerStateStore store = new(settings.Bridge.StatePath);
        ShoeEndpoint endpoint = new(settings.Shoes[0]);

        store.Apply(endpoint);

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Contains("could not be read", endpoint.AlignmentReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavedArmedRound_RestoresExactPhaseCardsAndStartIdentity()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        Guid eventUid = Guid.NewGuid();
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], eventUid);
        WorkerStateStore writer = new(settings.Bridge.StatePath);
        writer.Save(source);

        ShoeEndpointSettings restoredSettings = CloneEndpointSettings(settings.Shoes[0]);
        WorkerStateStore reader = new(settings.Bridge.StatePath);
        reader.Apply(restoredSettings);
        ShoeEndpoint restored = new(restoredSettings);
        reader.Apply(restored);

        Assert.Equal(BridgeRoundPhases.Dealing, restored.RoundPhase);
        Assert.Equal(BridgeBoundaryStrategies.DerivedAfterPreviousResult, restored.BoundaryStrategy);
        Assert.Equal(eventUid, restored.StartGameEventUid);
        Assert.Equal("Pending", restored.StartGameDeliveryState);
        Assert.Collection(
            restored.PlayerCards.OrderBy(card => card.Index),
            card => Assert.Equal((1, "Spade", "A"), (card.Index, card.Suit, card.Value)),
            card => Assert.Equal((2, "Heart", "10"), (card.Index, card.Suit, card.Value)));
        Assert.Collection(
            restored.BankerCards.OrderBy(card => card.Index),
            card => Assert.Equal((1, "Club", "K"), (card.Index, card.Suit, card.Value)),
            card => Assert.Equal((2, "Diamond", "2"), (card.Index, card.Suit, card.Value)));
    }

    [Fact]
    public void ArmedRoundWithUnknownStartDeliveryState_FailsClosed()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], Guid.NewGuid());
        ShoeRuntimeState corruptRuntime = source.CaptureRuntimeState() with
        {
            StartGameDeliveryState = "unexpected-state"
        };
        WorkerShoeState corruptState = new()
        {
            StateVersion = 2,
            DeskName = source.DeskName,
            SourceDataCode = source.SourceDataCode,
            ShoeId = source.ShoeId,
            CurrentShoe = source.CurrentShoe,
            CurrentRound = source.CurrentRound,
            CurrentRoundId = source.CurrentRoundId,
            Runtime = corruptRuntime,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(
            settings.Bridge.StatePath,
            JsonSerializer.Serialize(new[] { corruptState }, WorkerSettings.JsonOptions));

        WorkerStateStore store = new(settings.Bridge.StatePath);
        ShoeEndpoint restored = new(CloneEndpointSettings(settings.Shoes[0]));
        store.Apply(restored);

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, restored.RoundPhase);
        Assert.Contains("invalid", restored.AlignmentReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartMidRound_UsesRestoredCardsInTheRetainedGameResult()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        Guid startUid = Guid.NewGuid();
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], startUid);
        new WorkerStateStore(settings.Bridge.StatePath).Save(source);

        BridgeEventJournal seedJournal = new(settings.Bridge.DatabasePath);
        await seedJournal.AppendAsync(
            StartGamePayload(source, startUid),
            queueForDelivery: true);

        await using AngelBridgeWorker worker = new(settings);
        ShoeEndpoint restored = worker.Endpoints.Single();
        Assert.Equal(BridgeRoundPhases.Dealing, restored.RoundPhase);
        ConnectMock(restored);

        restored.Listener.InjectBytes(BuildActiveReport('1', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "GameResult") == 1,
            TimeSpan.FromSeconds(5));

        using JsonDocument payload = ReadLatestEventPayload(settings.Bridge.DatabasePath, "GameResult");
        JsonElement cards = payload.RootElement.GetProperty("data").GetProperty("cards");
        Assert.Equal("As", cards.GetProperty("p1").GetString());
        Assert.Equal("10h", cards.GetProperty("p2").GetString());
        Assert.Equal("Kc", cards.GetProperty("b1").GetString());
        Assert.Equal("2d", cards.GetProperty("b2").GetString());
    }

    [Fact]
    public async Task MissingDurableState_NormalResultCannotClearAlignmentOrStartNextRound()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        settings.Bridge.AutoStartNextRoundAfterResult = true;
        settings.Bridge.ResultToNextRoundDelaySeconds = 0;

        await using AngelBridgeWorker worker = new(settings);
        ShoeEndpoint endpoint = worker.Endpoints.Single();
        long originalRound = endpoint.CurrentRound;
        ConnectMock(endpoint);
        endpoint.PlayerCards.Add(new BaccaratCard { Index = 1, Suit = "Spade", Value = "A", IsPlayer = true });
        endpoint.PlayerCards.Add(new BaccaratCard { Index = 2, Suit = "Heart", Value = "10", IsPlayer = true });
        endpoint.BankerCards.Add(new BaccaratCard { Index = 1, Suit = "Club", Value = "K", IsPlayer = false });
        endpoint.BankerCards.Add(new BaccaratCard { Index = 2, Suit = "Diamond", Value = "2", IsPlayer = false });

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "GameResult") == 1,
            TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Equal(originalRound, endpoint.CurrentRound);
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, "StartGame"));
        Assert.Equal(
            "LocalOnly",
            ReadLatestEventStatus(settings.Bridge.DatabasePath, "GameResult"));
    }

    [Fact]
    public async Task MissingMandatoryCard_RetainsResultLocallyAndRequiresAlignment()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        Guid startUid = Guid.NewGuid();
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], startUid);
        source.BankerCards.RemoveAll(card => card.Index == 2);
        new WorkerStateStore(settings.Bridge.StatePath).Save(source);
        BridgeEventJournal seedJournal = new(settings.Bridge.DatabasePath);
        await seedJournal.AppendAsync(StartGamePayload(source, startUid), queueForDelivery: true);

        await using AngelBridgeWorker worker = new(settings);
        ShoeEndpoint restored = worker.Endpoints.Single();
        ConnectMock(restored);
        restored.Listener.InjectBytes(BuildActiveReport('1', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "GameResult") == 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            "LocalOnly",
            ReadLatestEventStatus(settings.Bridge.DatabasePath, "GameResult"));
        Assert.Equal(BridgeRoundPhases.AlignmentRequired, restored.RoundPhase);
        Assert.Contains("mandatory cards", restored.AlignmentReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawFrameWriteFailure_RejectsDecodedResultBeforeTranslation()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        Guid startUid = Guid.NewGuid();
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], startUid);
        new WorkerStateStore(settings.Bridge.StatePath).Save(source);
        BridgeEventJournal seedJournal = new(settings.Bridge.DatabasePath);
        await seedJournal.AppendAsync(StartGamePayload(source, startUid), queueForDelivery: true);

        await using AngelBridgeWorker worker = new(settings);
        ShoeEndpoint restored = worker.Endpoints.Single();
        ConnectMock(restored);
        ExecuteNonQuery(
            settings.Bridge.DatabasePath,
            "DROP TABLE bridge_raw_frames;");
        restored.Listener.InjectBytes(BuildActiveReport('1', (byte)'G', 0x91));

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, restored.RoundPhase);
        Assert.Contains("Raw frame", restored.AlignmentReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, "GameResult"));
    }

    [Fact]
    public void GameResultIdentity_IsStableForTheDurableStartGameIdentity()
    {
        Guid firstStart = Guid.NewGuid();
        Guid secondStart = Guid.NewGuid();

        Guid first = AngelBridgeWorker.DeriveGameResultEventUid(firstStart);

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, AngelBridgeWorker.DeriveGameResultEventUid(firstStart));
        Assert.NotEqual(first, AngelBridgeWorker.DeriveGameResultEventUid(secondStart));
    }

    [Fact]
    public async Task ForceQuit_IsRetainedLocallyAndNeverQueuedAsNormalGameResult()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: true);
        Guid startUid = Guid.NewGuid();
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], startUid);
        new WorkerStateStore(settings.Bridge.StatePath).Save(source);
        BridgeEventJournal seedJournal = new(settings.Bridge.DatabasePath);
        await seedJournal.AppendAsync(StartGamePayload(source, startUid), queueForDelivery: true);

        await using AngelBridgeWorker worker = new(settings);
        ShoeEndpoint endpoint = worker.Endpoints.Single();
        ConnectMock(endpoint);
        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'G', 0x70));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "GameResult") == 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            "LocalOnly",
            ReadLatestEventStatus(settings.Bridge.DatabasePath, "GameResult"));
        using JsonDocument payload = ReadLatestEventPayload(settings.Bridge.DatabasePath, "GameResult");
        Assert.Equal("Cancelled", payload.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(BridgeRoundPhases.Cancelled, worker.Endpoints.Single().RoundPhase);
    }

    [Fact]
    public async Task RawTransportBytes_AreStoredOutsideTheBmsEventOutbox()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: false);
        await using AngelBridgeWorker worker = new(settings);
        byte[] packet = BuildActiveReport('1', (byte)'D', 0x81, 0xB8);

        ShoeEndpoint endpoint = worker.Endpoints.Single();
        ConnectMock(endpoint);
        endpoint.Listener.InjectBytes(packet);
        await WaitUntilAsync(
            () => CountRows(settings.Bridge.DatabasePath, "bridge_raw_frames") == 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, CountEvents(settings.Bridge.DatabasePath, "CardDrawn"));
        Assert.Equal(
            BitConverter.ToString(packet).Replace("-", " "),
            ReadLatestRawHex(settings.Bridge.DatabasePath));
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, "RawFrame"));
    }

    [Fact]
    public async Task PersistedCutCardHold_RestoresAndStartSignalConfirmsNewShoe()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: false);
        long previousShoe;
        await using (AngelBridgeWorker firstWorker = new(settings))
        {
            ShoeEndpoint firstEndpoint = firstWorker.Endpoints.Single();
            previousShoe = firstEndpoint.CurrentShoe;
            firstEndpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));
            await WaitUntilAsync(
                () => CountEvents(settings.Bridge.DatabasePath, "CutCardDrawn") == 1,
                TimeSpan.FromSeconds(5));
            Assert.True(firstEndpoint.ShoeEnding);
            Assert.Equal(BridgeRoundPhases.ShoeChangePending, firstEndpoint.RoundPhase);
        }

        await using AngelBridgeWorker restoredWorker = new(settings);
        ShoeEndpoint restored = restoredWorker.Endpoints.Single();
        Assert.True(restored.ShoeEnding);
        Assert.Equal(BridgeRoundPhases.ShoeChangePending, restored.RoundPhase);

        restored.Listener.InjectBytes(BuildActiveReport('2', (byte)'S'));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "NewShoeConfirmed") == 1,
            TimeSpan.FromSeconds(5));
        long confirmedShoe = restored.CurrentShoe;
        restored.Listener.InjectBytes(BuildActiveReport('3', (byte)'S'));
        await Task.Delay(100);

        Assert.Equal(BridgeGameNumbering.NextShoe(previousShoe), confirmedShoe);
        Assert.Equal(0, restored.CurrentRound);
        Assert.False(restored.ShoeEnding);
        Assert.Equal(BridgeRoundPhases.ConnectedWaitingBoundary, restored.RoundPhase);
        Assert.True(restored.AwaitingFirstAuthoritativeResultAfterShoeChange);
        Assert.Equal(1, CountEvents(settings.Bridge.DatabasePath, "NewShoeConfirmed"));
    }

    [Fact]
    public async Task StartSignalDuringIncompleteOldRound_AuditsAndQuarantinesLateResult()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: false);
        Guid startUid = Guid.NewGuid();
        ShoeEndpoint source = CreateArmedEndpoint(settings.Shoes[0], startUid);
        WorkerStateStore store = new(settings.Bridge.StatePath);
        store.Save(source);
        BridgeEventJournal journal = new(settings.Bridge.DatabasePath);
        await journal.AppendAsync(
            StartGamePayload(source, startUid),
            queueForDelivery: false);

        await using AngelBridgeWorker worker = new(settings);
        ShoeEndpoint endpoint = worker.Endpoints.Single();
        long previousShoe = endpoint.CurrentShoe;
        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "CutCardDrawn") == 1,
            TimeSpan.FromSeconds(5));
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint.RoundPhase);

        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'S'));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "IncompleteAtShoeChange") == 1 &&
                  CountEvents(settings.Bridge.DatabasePath, "NewShoeConfirmed") == 1,
            TimeSpan.FromSeconds(5));
        long newShoe = endpoint.CurrentShoe;

        endpoint.Listener.InjectBytes(BuildActiveReport('3', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, "LateGameResultAfterShoeChange") == 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(BridgeGameNumbering.NextShoe(previousShoe), newShoe);
        Assert.Equal(newShoe, endpoint.CurrentShoe);
        Assert.Equal(0, endpoint.CurrentRound);
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, "GameResult"));
        Assert.True(endpoint.AwaitingFirstAuthoritativeResultAfterShoeChange);
    }

    [Fact]
    public async Task CorruptPersistedCardsJson_RejectsTheNextProjectionAndRollsBackTheEvent()
    {
        WorkerSettings settings = CreateSettings(bmsTransmitEnabled: false);
        BridgeEventJournal journal = new(settings.Bridge.DatabasePath);
        ShoeEndpoint endpoint = new(settings.Shoes[0]);
        Guid startUid = Guid.NewGuid();
        await journal.AppendAsync(StartGamePayload(endpoint, startUid), queueForDelivery: false);
        await journal.AppendAsync(
            CardPayload(endpoint, "Player", 1, "Spade", "A"),
            queueForDelivery: false);

        using (SqliteConnection connection = new($"Data Source={settings.Bridge.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE bridge_rounds
                SET cards_json = '{"not":"an-array"}'
                WHERE desk_id = $desk_id AND shoe = $shoe AND round = $round;
                """;
            command.Parameters.AddWithValue("$desk_id", endpoint.SourceDataCode);
            command.Parameters.AddWithValue("$shoe", endpoint.CurrentShoe);
            command.Parameters.AddWithValue("$round", endpoint.CurrentRound);
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.AppendAsync(
                CardPayload(endpoint, "Banker", 1, "Heart", "K"),
                queueForDelivery: false));

        Assert.Equal(2, CountRows(settings.Bridge.DatabasePath, "bridge_events"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.AppendAsync(
                GameResultPayload(endpoint),
                queueForDelivery: true));

        Assert.Equal(2, CountRows(settings.Bridge.DatabasePath, "bridge_events"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private WorkerSettings CreateSettings(bool bmsTransmitEnabled)
    {
        return new WorkerSettings
        {
            Bms = new BmsWorkerSettings
            {
                EventApiUrl = "https://127.0.0.1/api/source/angel/events",
                AutoGenerateJwt = false,
                ClientId = "state-test",
                ClientSecret = "test-client-secret"
            },
            Bridge = new BridgeWorkerSettings
            {
                InstanceName = "state-test",
                EnvironmentName = "Test",
                Role = "Worker",
                BridgeId = "state-test",
                BridgeName = "State test",
                DatabasePath = Path.Combine(_directory, "bridge-events.sqlite"),
                StatePath = Path.Combine(_directory, "bridge-state.json"),
                AutoConnect = false,
                AutoStartNextRoundAfterResult = false,
                ReadOnly = true
            },
            Health = new HealthWorkerSettings
            {
                Enabled = false
            },
            Shoes =
            [
                new ShoeEndpointSettings
                {
                    Enabled = true,
                    BmsTransmitEnabled = bmsTransmitEnabled,
                    DeskName = "901桌",
                    SourceDataCode = "901",
                    SourceDataId = Guid.NewGuid().ToString("D"),
                    ShoeId = "SHOE901",
                    CurrentShoe = 202607260001,
                    CurrentRound = 8,
                    CurrentRoundId = 8,
                    ConnectionMode = ShoeConnectionMode.MoxaTcp,
                    MoxaHost = "127.0.0.1",
                    MoxaPort = 4001
                }
            ]
        };
    }

    private static ShoeEndpoint CreateArmedEndpoint(ShoeEndpointSettings settings, Guid eventUid)
    {
        ShoeEndpoint endpoint = new(CloneEndpointSettings(settings));
        endpoint.ArmRoundBoundary(
            BridgeBoundaryStrategies.DerivedAfterPreviousResult,
            DateTimeOffset.UtcNow.AddSeconds(-10),
            eventUid);
        endpoint.MarkStartGameStored("Pending");
        endpoint.PlayerCards.Add(new BaccaratCard { Index = 1, Suit = "Spade", Value = "A", IsPlayer = true });
        endpoint.PlayerCards.Add(new BaccaratCard { Index = 2, Suit = "Heart", Value = "10", IsPlayer = true });
        endpoint.BankerCards.Add(new BaccaratCard { Index = 1, Suit = "Club", Value = "K", IsPlayer = false });
        endpoint.BankerCards.Add(new BaccaratCard { Index = 2, Suit = "Diamond", Value = "2", IsPlayer = false });
        endpoint.MarkDealing();
        return endpoint;
    }

    private static void ConnectMock(ShoeEndpoint endpoint)
    {
        endpoint.MockMode = true;
        endpoint.Connect();
    }

    private static ShoeEndpointSettings CloneEndpointSettings(ShoeEndpointSettings source) => new()
    {
        Enabled = source.Enabled,
        BmsTransmitEnabled = source.BmsTransmitEnabled,
        DeskName = source.DeskName,
        SourceDataCode = source.SourceDataCode,
        SourceDataId = source.SourceDataId,
        ShoeId = source.ShoeId,
        CurrentShoe = source.CurrentShoe,
        CurrentRound = source.CurrentRound,
        CurrentRoundId = source.CurrentRoundId,
        ConnectionMode = source.ConnectionMode,
        MoxaHost = source.MoxaHost,
        MoxaPort = source.MoxaPort
    };

    private static Dictionary<string, object?> StartGamePayload(ShoeEndpoint endpoint, Guid eventUid) => new()
    {
        ["bridgeId"] = "state-test",
        ["eventUid"] = eventUid,
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["type"] = "StartGame",
        ["source"] = AngelEyeProtocol.SourceName,
        ["sourceDataCode"] = endpoint.SourceDataCode,
        ["sourceDataId"] = endpoint.SourceDataId,
        ["deviceId"] = endpoint.DeviceId,
        ["shoe"] = endpoint.CurrentShoe,
        ["round"] = endpoint.CurrentRound,
        ["roundId"] = endpoint.CurrentRoundId,
        ["data"] = new Dictionary<string, object?>()
    };

    private static Dictionary<string, object?> CardPayload(
        ShoeEndpoint endpoint,
        string target,
        int index,
        string suit,
        string value) => new()
    {
        ["bridgeId"] = "state-test",
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["type"] = "CardDrawn",
        ["source"] = AngelEyeProtocol.SourceName,
        ["sourceDataCode"] = endpoint.SourceDataCode,
        ["sourceDataId"] = endpoint.SourceDataId,
        ["deviceId"] = endpoint.DeviceId,
        ["shoe"] = endpoint.CurrentShoe,
        ["round"] = endpoint.CurrentRound,
        ["roundId"] = endpoint.CurrentRoundId,
        ["data"] = new
        {
            eventCode = "D",
            accepted = true,
            target,
            index,
            suit,
            value
        }
    };

    private static Dictionary<string, object?> GameResultPayload(
        ShoeEndpoint endpoint) => new()
    {
        ["bridgeId"] = "state-test",
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["type"] = "GameResult",
        ["source"] = AngelEyeProtocol.SourceName,
        ["sourceDataCode"] = endpoint.SourceDataCode,
        ["sourceDataId"] = endpoint.SourceDataId,
        ["deviceId"] = endpoint.DeviceId,
        ["shoe"] = endpoint.CurrentShoe,
        ["round"] = endpoint.CurrentRound,
        ["roundId"] = endpoint.CurrentRoundId,
        ["data"] = new
        {
            result = "PlayerWin",
            pair = "None",
            status = "Normal",
            cards = new
            {
                p1 = "As",
                p2 = "10h",
                p3 = "",
                b1 = "Kc",
                b2 = "2d",
                b3 = ""
            }
        }
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (!predicate())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellation.Token);
        }
    }

    private static int CountRows(string dbPath, string table)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void ExecuteNonQuery(string dbPath, string sql)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int CountEvents(string dbPath, string type)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM bridge_events WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ReadLatestEventStatus(string dbPath, string type)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM bridge_events
            WHERE type = $type
            ORDER BY event_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", type);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static JsonDocument ReadLatestEventPayload(string dbPath, string type)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM bridge_events
            WHERE type = $type
            ORDER BY event_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", type);
        return JsonDocument.Parse(Assert.IsType<string>(command.ExecuteScalar()));
    }

    private static string ReadLatestRawHex(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_hex
            FROM bridge_raw_frames
            ORDER BY raw_frame_id DESC
            LIMIT 1;
            """;
        return Assert.IsType<string>(command.ExecuteScalar());
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
