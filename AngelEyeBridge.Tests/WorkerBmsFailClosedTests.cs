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
    public async Task NonBoundaryCardBeforeStartGame_KeepsCardAndResultLocalOnly()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true));
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        long initialShoe = worker.Endpoints[0].CurrentShoe;
        long initialRound = worker.Endpoints[0].CurrentRound;

        worker.Endpoints[0].Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x82, 0xB8));
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
    public async Task ColdStartMidRound_FromSecondCards_KeepsPartialRoundLocalAndAlignmentRequired()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true));
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];
        long initialShoe = endpoint.CurrentShoe;
        long initialRound = endpoint.CurrentRound;

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x82, 0xB8));
        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0x92, 0x4D));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "CardDrawn") == 2,
            cancellation.Token);

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Contains("before a durable StartGame", endpoint.AlignmentReason);
        Assert.Collection(
            endpoint.PlayerCards,
            card => Assert.Equal(2, card.Index));
        Assert.Collection(
            endpoint.BankerCards,
            card => Assert.Equal(2, card.Index));

        endpoint.Listener.InjectBytes(BuildActiveReport('3', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);

        Assert.Equal(initialShoe, endpoint.CurrentShoe);
        Assert.Equal(initialRound, endpoint.CurrentRound);
        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));
        Assert.Equal(
            2,
            CountEvents(settings.Bridge.DatabasePath, type: "CardDrawn", status: "LocalOnly"));
        Assert.Equal(
            1,
            CountEvents(settings.Bridge.DatabasePath, type: "GameResult", status: "LocalOnly"));
        Assert.Equal(3, CountRows(settings.Bridge.DatabasePath, "bridge_raw_frames"));
        Assert.Empty(await worker.Journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
    }

    [Fact]
    public async Task PlayerOneBoundary_PersistsZeroBetStartGameBeforeCard_OnlyOnce()
    {
        ShoeEndpointSettings endpointSettings =
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpointSettings.TotalBetTimeSeconds = 0;
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];

        byte[] playerOne = BuildActiveReport('1', (byte)'D', 0x81, 0xB8);
        endpoint.Listener.InjectBytes(playerOne);
        endpoint.Listener.InjectBytes(playerOne);
        endpoint.Listener.InjectBytes(playerOne);
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 1 &&
                  CountEvents(settings.Bridge.DatabasePath, type: "CardDrawn") == 1,
            cancellation.Token);

        Assert.Equal(2, endpoint.CurrentRound);
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint.RoundPhase);
        Assert.Equal(BridgeBoundaryStrategies.VerifiedDeviceSignal, endpoint.BoundaryStrategy);
        Assert.Equal(0, endpoint.TotalBetTimeSeconds);
        Assert.Equal(
            ["StartGame", "CardDrawn"],
            ReadEventTypes(settings.Bridge.DatabasePath));
        using JsonDocument startGame = ReadEventPayload(
            settings.Bridge.DatabasePath,
            "StartGame");
        Assert.Equal(0, startGame.RootElement.GetProperty("totalBetTime").GetInt32());
        Assert.Equal(
            0,
            startGame.RootElement
                .GetProperty("data")
                .GetProperty("totalBetTime")
                .GetInt32());
    }

    [Fact]
    public async Task RestoredOldDate_BurnDoesNotAdvance_FirstPlayerOneStartsCurrentDateFirstShoe()
    {
        ShoeEndpointSettings endpointSettings =
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpointSettings.CurrentShoe = 202607270001;
        endpointSettings.CurrentRound = 1;
        endpointSettings.CurrentRoundId = 1;
        endpointSettings.TotalBetTimeSeconds = 0;
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        new WorkerStateStore(settings.Bridge.StatePath).Save(new ShoeEndpoint(endpointSettings));

        FixedTimeProvider clock = new(
            new DateTimeOffset(2026, 7, 29, 6, 6, 0, TimeSpan.Zero),
            TimeSpan.FromHours(8));
        await using AngelBridgeWorker worker = new(settings, clock);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0xC0, 0xB8));
        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0xD9, 0x4D));
        await Task.Delay(100, cancellation.Token);

        Assert.Equal(202607270001, endpoint.CurrentShoe);
        Assert.Equal(1, endpoint.CurrentRound);
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));

        endpoint.Listener.InjectBytes(BuildActiveReport('3', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 1,
            cancellation.Token);

        Assert.Equal(202607290001, endpoint.CurrentShoe);
        Assert.Equal(1, endpoint.CurrentRound);
        Assert.Equal(
            [(202607290001L, 1L)],
            ReadEventIdentities(settings.Bridge.DatabasePath, "StartGame"));

        endpoint.Listener.InjectBytes(BuildActiveReport('4', (byte)'D', 0x91, 0x4D));
        endpoint.Listener.InjectBytes(BuildActiveReport('5', (byte)'D', 0x82, 0xB9));
        endpoint.Listener.InjectBytes(BuildActiveReport('6', (byte)'D', 0x92, 0x41));
        endpoint.Listener.InjectBytes(BuildActiveReport('7', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);

        Assert.Equal(
            [(202607290001L, 1L)],
            ReadEventIdentities(settings.Bridge.DatabasePath, "GameResult"));

        endpoint.Listener.InjectBytes(BuildActiveReport('8', (byte)'D', 0x81, 0xB7));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 2,
            cancellation.Token);

        Assert.Equal(202607290001, endpoint.CurrentShoe);
        Assert.Equal(2, endpoint.CurrentRound);
        Assert.Equal(
            [(202607290001L, 1L), (202607290001L, 2L)],
            ReadEventIdentities(settings.Bridge.DatabasePath, "StartGame"));
    }

    [Fact]
    public async Task RestoredSameDate_DoesNotResetShoeOrRoundUntilNextPlayerOne()
    {
        ShoeEndpointSettings endpointSettings =
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpointSettings.CurrentShoe = 202607290001;
        endpointSettings.CurrentRound = 5;
        endpointSettings.CurrentRoundId = 5;
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        new WorkerStateStore(settings.Bridge.StatePath).Save(new ShoeEndpoint(endpointSettings));

        FixedTimeProvider clock = new(
            new DateTimeOffset(2026, 7, 29, 6, 6, 0, TimeSpan.Zero),
            TimeSpan.FromHours(8));
        await using AngelBridgeWorker worker = new(settings, clock);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0xC0, 0xB8));
        await Task.Delay(100, cancellation.Token);
        Assert.Equal(202607290001, endpoint.CurrentShoe);
        Assert.Equal(5, endpoint.CurrentRound);

        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0x81, 0xB9));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 1,
            cancellation.Token);

        Assert.Equal(202607290001, endpoint.CurrentShoe);
        Assert.Equal(6, endpoint.CurrentRound);
    }

    [Fact]
    public async Task LegacyResultTimerSetting_DoesNotCreatePhantomNextRound()
    {
        ShoeEndpointSettings endpointSettings =
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpointSettings.TotalBetTimeSeconds = 0;
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        settings.Bridge.AutoStartNextRoundAfterResult = true;
        settings.Bridge.ResultToNextRoundDelaySeconds = 1;
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x81, 0xB8));
        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0x91, 0x4D));
        endpoint.Listener.InjectBytes(BuildActiveReport('3', (byte)'D', 0x82, 0xB9));
        endpoint.Listener.InjectBytes(BuildActiveReport('4', (byte)'D', 0x92, 0x41));
        endpoint.Listener.InjectBytes(BuildActiveReport('5', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);
        long completedRound = endpoint.CurrentRound;
        await Task.Delay(TimeSpan.FromMilliseconds(1300), cancellation.Token);

        Assert.Equal(completedRound, endpoint.CurrentRound);
        Assert.Equal(1, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));
        Assert.Equal(
            [(endpoint.CurrentShoe, completedRound)],
            ReadEventIdentities(settings.Bridge.DatabasePath, "StartGame"));
    }

    [Fact]
    public async Task MidRoundStartup_SkipsPartialRound_ThenNextPlayerOneRealigns()
    {
        ShoeEndpointSettings endpointSettings =
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpointSettings.TotalBetTimeSeconds = 0;
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x82, 0xB8));
        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0x92, 0x4D));
        endpoint.Listener.InjectBytes(BuildActiveReport('3', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);

        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Equal(0, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));

        endpoint.Listener.InjectBytes(BuildActiveReport('4', (byte)'D', 0x81, 0xB9));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 1,
            cancellation.Token);

        Assert.Equal(2, endpoint.CurrentRound);
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint.RoundPhase);
        Assert.Single(endpoint.PlayerCards);
        Assert.Equal(1, endpoint.PlayerCards[0].Index);
        Assert.Empty(endpoint.BankerCards);
    }

    [Fact]
    public async Task SamePlayerOneCardInFollowingRound_IsANewBoundary()
    {
        ShoeEndpointSettings endpointSettings =
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true);
        endpointSettings.TotalBetTimeSeconds = 0;
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint = worker.Endpoints[0];

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x81, 0xB8));
        endpoint.Listener.InjectBytes(BuildActiveReport('2', (byte)'D', 0x91, 0x4D));
        endpoint.Listener.InjectBytes(BuildActiveReport('3', (byte)'D', 0x82, 0xB9));
        endpoint.Listener.InjectBytes(BuildActiveReport('4', (byte)'D', 0x92, 0x41));
        endpoint.Listener.InjectBytes(BuildActiveReport('5', (byte)'G', 0x91));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "GameResult") == 1,
            cancellation.Token);
        long completedRound = endpoint.CurrentRound;
        await Task.Delay(100, cancellation.Token);
        Assert.Equal(completedRound, endpoint.CurrentRound);

        endpoint.Listener.InjectBytes(BuildActiveReport('6', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 2,
            cancellation.Token);

        Assert.Equal(completedRound + 1, endpoint.CurrentRound);
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint.RoundPhase);
        Assert.Single(endpoint.PlayerCards);
        Assert.Equal(("Spade", "8"), (endpoint.PlayerCards[0].Suit, endpoint.PlayerCards[0].Value));
    }

    [Fact]
    public async Task PlayerOneBoundary_AdvancesOnlyItsOwnEndpoint()
    {
        ShoeEndpointSettings[] endpointSettings =
        [
            Endpoint("901", "SHOE901", bmsTransmitEnabled: false),
            Endpoint("902", "SHOE902", bmsTransmitEnabled: false),
            Endpoint("903", "SHOE903", bmsTransmitEnabled: false),
            Endpoint("904", "SHOE904", bmsTransmitEnabled: false)
        ];
        foreach (ShoeEndpointSettings endpoint in endpointSettings)
        {
            endpoint.TotalBetTimeSeconds = 0;
        }

        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            endpointSettings);
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint902 =
            worker.Endpoints.Single(endpoint => endpoint.SourceDataCode == "902");

        endpoint902.Listener.InjectBytes(BuildActiveReport('1', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 1,
            cancellation.Token);

        Assert.All(
            worker.Endpoints.Where(endpoint => endpoint.SourceDataCode != "902"),
            endpoint => Assert.Equal(1, endpoint.CurrentRound));
        Assert.Equal(2, endpoint902.CurrentRound);
        Assert.Equal(
            1,
            CountEvents(
                settings.Bridge.DatabasePath,
                type: "StartGame",
                sourceDataCode: "902"));
    }

    [Fact]
    public async Task CutThenStartSignal_AutomaticallyChangesOnlyTheSameEndpointOnce()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: true,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: false),
            Endpoint("902", "SHOE902", bmsTransmitEnabled: false));
        await using AngelBridgeWorker worker = new(settings);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ShoeEndpoint endpoint901 = worker.Endpoints.Single(endpoint => endpoint.SourceDataCode == "901");
        ShoeEndpoint endpoint902 = worker.Endpoints.Single(endpoint => endpoint.SourceDataCode == "902");
        long initial901Shoe = endpoint901.CurrentShoe;
        long initial902Shoe = endpoint902.CurrentShoe;

        endpoint902.Listener.InjectBytes(BuildActiveReport('1', (byte)'S'));
        await Task.Delay(100, cancellation.Token);
        Assert.Equal(initial902Shoe, endpoint902.CurrentShoe);

        endpoint901.Listener.InjectBytes(BuildActiveReport('2', (byte)'C'));
        endpoint901.Listener.InjectBytes(BuildActiveReport('3', (byte)'C'));
        await WaitUntilAsync(() => endpoint901.ShoeEnding, cancellation.Token);
        endpoint901.Listener.InjectBytes(BuildActiveReport('4', (byte)'S'));
        await WaitUntilAsync(
            () => CountEvents(
                settings.Bridge.DatabasePath,
                type: "NewShoeConfirmed",
                status: "LocalOnly") == 1,
            cancellation.Token);
        long confirmedShoe = endpoint901.CurrentShoe;
        endpoint901.Listener.InjectBytes(BuildActiveReport('5', (byte)'S'));
        await Task.Delay(100, cancellation.Token);

        Assert.Equal(BridgeGameNumbering.NextShoe(initial901Shoe), confirmedShoe);
        Assert.Equal(0, endpoint901.CurrentRound);
        Assert.False(endpoint901.ShoeEnding);
        Assert.Equal(BridgeRoundPhases.ConnectedWaitingBoundary, endpoint901.RoundPhase);
        Assert.True(endpoint901.AwaitingFirstAuthoritativeResultAfterShoeChange);
        Assert.Equal(initial902Shoe, endpoint902.CurrentShoe);
        Assert.Equal(1, endpoint902.CurrentRound);
        Assert.False(endpoint902.ShoeEnding);
        Assert.Equal(
            1,
            CountEvents(
                settings.Bridge.DatabasePath,
                type: "NewShoeConfirmed",
                status: "LocalOnly"));

        endpoint901.Listener.InjectBytes(BuildActiveReport('6', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(
            () => CountEvents(settings.Bridge.DatabasePath, type: "StartGame") == 1,
            cancellation.Token);
        Assert.Equal(1, endpoint901.CurrentRound);
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint901.RoundPhase);
    }

    [Fact]
    public async Task PlayerOneDuringResultDelay_ReplacesDerivedBoundaryWithoutDoubleStart()
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

        Assert.Equal(1, CountEvents(settings.Bridge.DatabasePath, type: "StartGame"));
        Assert.Equal(
            BridgeBoundaryStrategies.VerifiedDeviceSignal,
            worker.Endpoints[0].BoundaryStrategy);
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
    public async Task BmsControlPlaneSnapshot_ExcludesLocalOnlyAndDisabledEndpoints()
    {
        WorkerSettings settings = CreateSettings(
            readOnly: false,
            healthPort: null,
            Endpoint("901", "SHOE901", bmsTransmitEnabled: true),
            Endpoint("ANGEL_BACQA", "SHOEQA", bmsTransmitEnabled: false),
            Endpoint("903", "SHOE903", bmsTransmitEnabled: true, enabled: false));

        await using AngelBridgeWorker worker = new(settings);

        Assert.Equal(3, worker.Endpoints.Count);
        Assert.Contains(
            worker.Endpoints,
            endpoint =>
                endpoint.SourceDataCode == "ANGEL_BACQA" &&
                endpoint.Enabled &&
                !endpoint.BmsTransmitEnabled);

        AngelBridgeHeartbeatEndpointStatus endpointStatus =
            Assert.Single(worker.BuildHeartbeatSnapshot());
        Assert.Equal("901", endpointStatus.SourceDataCode);
        Assert.Equal("SHOE901", endpointStatus.DeviceId);
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
        CurrentShoe = BridgeGameNumbering.TodayFirstShoe(),
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

    private static int CountEvents(
        string dbPath,
        string? type = null,
        string? status = null,
        string? sourceDataCode = null)
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
        if (!string.IsNullOrWhiteSpace(sourceDataCode))
        {
            clauses.Add("json_extract(payload_json, '$.sourceDataCode') = $source_data_code");
            command.Parameters.AddWithValue("$source_data_code", sourceDataCode);
        }
        command.CommandText = "SELECT COUNT(*) FROM bridge_events" +
            (clauses.Count == 0 ? ";" : $" WHERE {string.Join(" AND ", clauses)};");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string[] ReadEventTypes(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM bridge_events ORDER BY event_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> types = [];
        while (reader.Read())
        {
            types.Add(reader.GetString(0));
        }

        return types.ToArray();
    }

    private static (long Shoe, long Round)[] ReadEventIdentities(string dbPath, string type)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT shoe, round FROM bridge_events WHERE type = $type ORDER BY event_id;";
        command.Parameters.AddWithValue("$type", type);
        using SqliteDataReader reader = command.ExecuteReader();
        List<(long Shoe, long Round)> identities = [];
        while (reader.Read())
        {
            identities.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return identities.ToArray();
    }

    private static JsonDocument ReadEventPayload(string dbPath, string type)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT payload_json FROM bridge_events WHERE type = $type ORDER BY event_id LIMIT 1;";
        command.Parameters.AddWithValue("$type", type);
        return JsonDocument.Parse(Assert.IsType<string>(command.ExecuteScalar()));
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        private readonly TimeZoneInfo _localTimeZone;

        public FixedTimeProvider(DateTimeOffset utcNow, TimeSpan localOffset)
        {
            _utcNow = utcNow;
            _localTimeZone = TimeZoneInfo.CreateCustomTimeZone(
                "AngelEye-Test-TimeZone",
                localOffset,
                "AngelEye Test Time Zone",
                "AngelEye Test Time Zone");
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override TimeZoneInfo LocalTimeZone => _localTimeZone;
    }
}
