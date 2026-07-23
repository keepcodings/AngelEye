using System.Text;
using System.Threading.Channels;
using System.Net.Sockets;
using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class MoxaLiveMonitorTests
{
    [Fact]
    public void DecoderAndSerialListener_ProduceTheSameOrderedEvents()
    {
        var listener = new SerialListener();
        var decoder = new AngelEyeFrameDecoder();
        var listenerEvents = new List<string>();
        var decoderEvents = new List<string>();

        listener.OnCardDrawn += card =>
            listenerEvents.Add($"card:{card.Seq}:{card.Target}:{card.Value}:{card.Suit}");
        listener.OnGameResult += result =>
            listenerEvents.Add($"result:{result.Seq}:{result.Result}:{result.Pair}");
        decoder.CardDrawn += card =>
            decoderEvents.Add($"card:{card.Seq}:{card.Target}:{card.Value}:{card.Suit}");
        decoder.GameResultReceived += result =>
            decoderEvents.Add($"result:{result.Seq}:{result.Result}:{result.Pair}");

        byte[] input = BuildActiveReport('1', (byte)'D', 0x81, 0xB8)
            .Concat(BuildActiveReport('2', (byte)'G', 0x91))
            .ToArray();

        listener.InjectBytes(input);
        decoder.Feed(input);

        Assert.Equal(listenerEvents, decoderEvents);
    }

    [Fact]
    public void ReceiveTransportSurface_HasNoWriteOrCommandCapability()
    {
        string[] methodNames = typeof(IMoxaReceiveTransport)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IMoxaReceiveTransport.ConnectAsync), methodNames);
        Assert.Contains(nameof(IMoxaReceiveTransport.ReceiveAsync), methodNames);
        Assert.Contains(nameof(IMoxaReceiveTransport.StopAsync), methodNames);
        Assert.DoesNotContain(methodNames, name =>
            name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalog_IsTheExplicitFourEndpointAllowList()
    {
        Assert.Equal(
            new[]
            {
                "901|10.5.32.24:4001",
                "902|10.5.32.25:4001",
                "903|10.5.32.26:4001",
                "QA|10.5.32.124:4001"
            },
            MoxaMonitorCatalog.Endpoints.Select(endpoint =>
                $"{endpoint.Desk}|{endpoint.Key}"));
    }

    [Fact]
    public async Task Session_StartIsExplicitAndIdempotent_AndStopDisposesTransport()
    {
        var transport = new FakeReceiveTransport();
        var session = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[0],
            () => transport);

        Assert.Equal(0, transport.ConnectCount);
        Assert.Equal(MoxaMonitorConnectionState.Stopped, session.Snapshot.State);

        await session.StartAsync();
        await transport.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.StartAsync();

        Assert.Equal(1, transport.ConnectCount);
        Assert.Equal(MoxaMonitorConnectionState.Live, session.Snapshot.State);

        await session.StopAsync();

        Assert.Equal(MoxaMonitorConnectionState.Stopped, session.Snapshot.State);
        Assert.True(transport.StopCount >= 1);
    }

    [Fact]
    public async Task Session_UsesCutCardAsCompleteBoundaryAndRetainsLatestCards()
    {
        var transport = new FakeReceiveTransport();
        var session = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[1],
            () => transport);
        await session.StartAsync();
        await transport.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        transport.Enqueue(BuildActiveReport('1', (byte)'C'));
        transport.Enqueue(BuildActiveReport('2', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(() => session.Snapshot.PlayerCards.Count == 1);

        MoxaMonitorSnapshot snapshot = session.Snapshot;
        Assert.False(snapshot.Partial);
        Assert.Equal(new[] { "8♠" }, snapshot.PlayerCards);
        Assert.Empty(snapshot.BankerCards);
        Assert.Equal(1, snapshot.CutCardCount);
        Assert.Null(typeof(MoxaMonitorSnapshot).GetProperty("Shoe"));
        Assert.Null(typeof(MoxaMonitorSnapshot).GetProperty("Round"));
        Assert.Null(typeof(MoxaMonitorSnapshot).GetProperty("RoundId"));

        await session.StopAsync();
    }

    [Fact]
    public async Task Session_BoundsDiagnosticsAndCountsDroppedRows()
    {
        var transport = new FakeReceiveTransport();
        var session = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[2],
            () => transport);
        await session.StartAsync();
        await transport.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 0; index < 225; index++)
        {
            transport.Enqueue([0x00]);
        }

        await WaitUntilAsync(() => session.Snapshot.DroppedDiagnostics >= 25);
        MoxaMonitorSnapshot snapshot = session.Snapshot;
        Assert.Equal(200, snapshot.Diagnostics.Count);
        Assert.True(snapshot.DroppedDiagnostics >= 25);

        await session.StopAsync();
    }

    [Fact]
    public async Task Session_ReconnectsWithPartialEvidenceAndDisposesFailedTransport()
    {
        var failed = new FailingReceiveTransport();
        var recovered = new FakeReceiveTransport();
        var transports = new Queue<IMoxaReceiveTransport>([failed, recovered]);
        var session = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[3],
            () => transports.Dequeue(),
            [TimeSpan.FromMilliseconds(10)]);

        await session.StartAsync();
        await recovered.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.Snapshot.State == MoxaMonitorConnectionState.Live);

        Assert.True(failed.StopCount >= 1);
        Assert.True(session.Snapshot.Partial);
        Assert.Equal(1, session.Snapshot.SessionEpoch);

        await session.StopAsync();
    }

    [Fact]
    public async Task Session_StopCancelsLongBackoffImmediately()
    {
        var failed = new FailingReceiveTransport();
        var session = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[0],
            () => failed,
            [TimeSpan.FromMinutes(1)]);

        await session.StartAsync();
        await WaitUntilAsync(() => session.Snapshot.State == MoxaMonitorConnectionState.Backoff);
        Task stop = session.StopAsync();

        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MoxaMonitorConnectionState.Stopped, session.Snapshot.State);
    }

    [Fact]
    public async Task EndpointFailure_DoesNotClearAnotherEndpointSnapshot()
    {
        var liveTransport = new FakeReceiveTransport();
        var healthy = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[0],
            () => liveTransport);
        var failed = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[1],
            () => new FailingReceiveTransport(),
            [TimeSpan.FromMinutes(1)]);

        await healthy.StartAsync();
        await liveTransport.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        liveTransport.Enqueue(BuildActiveReport('1', (byte)'C'));
        liveTransport.Enqueue(BuildActiveReport('2', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(() => healthy.Snapshot.PlayerCards.Count == 1);

        await failed.StartAsync();
        await WaitUntilAsync(() => failed.Snapshot.State == MoxaMonitorConnectionState.Backoff);

        Assert.Equal(new[] { "8♠" }, healthy.Snapshot.PlayerCards);
        Assert.Equal(MoxaMonitorConnectionState.Live, healthy.Snapshot.State);
        Assert.Equal(MoxaMonitorConnectionState.Backoff, failed.Snapshot.State);

        await failed.StopAsync();
        await healthy.StopAsync();
    }

    [Fact]
    public async Task MonitorTraffic_DoesNotMutateWorkerQueryEvidenceOrOwnBmsDependencies()
    {
        var queryState = new QueryConsoleState();
        var workerStatus = new QueryStatusData(
            "worker-qa",
            "QA",
            "1.0",
            true,
            [
                new QueryEndpointData(
                    "901", "901", "SHOE901", true, true, "Live",
                    DateTimeOffset.UtcNow, "GameResult", 20260723001, 12, 99,
                    true, 3, 0)
            ]);
        queryState.MarkSuccess(workerStatus, DateTimeOffset.UtcNow);

        var transport = new FakeReceiveTransport();
        var session = new MoxaMonitorSession(
            MoxaMonitorCatalog.Endpoints[0],
            () => transport);
        await session.StartAsync();
        await transport.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.Enqueue(BuildActiveReport('1', (byte)'C'));
        transport.Enqueue(BuildActiveReport('2', (byte)'D', 0x81, 0xB8));
        await WaitUntilAsync(() => session.Snapshot.PlayerCards.Count == 1);

        Assert.Same(workerStatus, queryState.LastStatus);
        Assert.Equal(3, queryState.LastStatus!.Endpoints[0].PendingCount);
        Assert.DoesNotContain(
            typeof(MoxaMonitorSession)
                .GetFields(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                .Select(field => field.FieldType.Name),
            typeName =>
                typeName.Contains("Bms", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Journal", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("TeleBet", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Http", StringComparison.OrdinalIgnoreCase));

        await session.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static byte[] BuildActiveReport(char sequence, params byte[] data)
    {
        var packet = new byte[data.Length + 3];
        packet[0] = 0x05;
        packet[1] = (byte)sequence;
        data.CopyTo(packet, 2);
        packet[^1] = 0x03;
        return packet
            .Concat(Encoding.ASCII.GetBytes(AngelEyeFrameDecoder.CalculateBcc(packet)))
            .ToArray();
    }

    private sealed class FakeReceiveTransport : IMoxaReceiveTransport
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        public TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectCount { get; private set; }
        public int StopCount { get; private set; }

        public ValueTask ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            Connected.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            byte[] chunk = await _chunks.Reader.ReadAsync(cancellationToken);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public ValueTask StopAsync()
        {
            StopCount++;
            _chunks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => StopAsync();

        public void Enqueue(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
    }

    private sealed class FailingReceiveTransport : IMoxaReceiveTransport
    {
        public int StopCount { get; private set; }

        public ValueTask ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused));

        public ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<int>(new InvalidOperationException("Not connected."));

        public ValueTask StopAsync()
        {
            StopCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => StopAsync();
    }
}
