using System.Collections.ObjectModel;
using System.Net.Sockets;

namespace AngelEyeBmsBridge;

public sealed record MoxaMonitorEndpoint(string Desk, string Host, int Port)
{
    public string Key => $"{Host}:{Port}";
}

public static class MoxaMonitorCatalog
{
    public static readonly IReadOnlyList<MoxaMonitorEndpoint> Endpoints =
        new ReadOnlyCollection<MoxaMonitorEndpoint>(
        [
            new("901", "10.5.32.24", 4001),
            new("902", "10.5.32.25", 4001),
            new("903", "10.5.32.26", 4001),
            new("QA", "10.5.32.124", 4001)
        ]);
}

public enum MoxaMonitorConnectionState
{
    Stopped,
    Connecting,
    Live,
    Backoff,
    Failed
}

public sealed record MoxaMonitorDiagnostic(
    DateTimeOffset At,
    string Kind,
    string Message);

public sealed record MoxaMonitorSnapshot(
    MoxaMonitorEndpoint Endpoint,
    long SessionEpoch,
    MoxaMonitorConnectionState State,
    DateTimeOffset? SessionStartedAt,
    DateTimeOffset? LastFrameAt,
    bool Partial,
    IReadOnlyList<string> PlayerCards,
    IReadOnlyList<string> BankerCards,
    string GameResult,
    int GameResultCount,
    int CutCardCount,
    string Error,
    long DroppedDiagnostics,
    IReadOnlyList<MoxaMonitorDiagnostic> Diagnostics,
    string StatusMessage)
{
    public static MoxaMonitorSnapshot Stopped(MoxaMonitorEndpoint endpoint) =>
        new(
            endpoint,
            0,
            MoxaMonitorConnectionState.Stopped,
            null,
            null,
            true,
            [],
            [],
            string.Empty,
            0,
            0,
            string.Empty,
            0,
            [],
            "尚未開始");
}

/// <summary>
/// Receive-only transport boundary. Deliberately exposes no write operation.
/// </summary>
public interface IMoxaReceiveTransport : IAsyncDisposable
{
    ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask StopAsync();
}

public sealed class TcpMoxaReceiveTransport : IMoxaReceiveTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public async ValueTask ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, cancellationToken);
        _stream = _client.GetStream();
    }

    public async ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("MOXA receive transport is not connected.");
        }

        return await _stream.ReadAsync(buffer, cancellationToken);
    }

    public ValueTask StopAsync()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class MoxaMonitorSession : IAsyncDisposable
{
    private const int DiagnosticCapacity = 200;
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private readonly object _sync = new();
    private readonly Func<IMoxaReceiveTransport> _transportFactory;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Queue<MoxaMonitorDiagnostic> _diagnostics = new();
    private readonly AngelEyeFrameDecoder _decoder = new();
    private CancellationTokenSource? _sessionCts;
    private Task? _runTask;
    private IMoxaReceiveTransport? _transport;
    private long _epoch;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastFrameAt;
    private MoxaMonitorConnectionState _state = MoxaMonitorConnectionState.Stopped;
    private bool _partial = true;
    private readonly List<string> _playerCards = [];
    private readonly List<string> _bankerCards = [];
    private string _gameResult = string.Empty;
    private int _gameResultCount;
    private int _cutCardCount;
    private string _error = string.Empty;
    private long _dropped;
    private byte? _lastSequence;
    private string _statusMessage = "尚未開始";

    public MoxaMonitorSession(
        MoxaMonitorEndpoint endpoint,
        Func<IMoxaReceiveTransport>? transportFactory = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        Endpoint = endpoint;
        _transportFactory = transportFactory ?? (() => new TcpMoxaReceiveTransport());
        _retryDelays = retryDelays ?? DefaultRetryDelays;

        _decoder.CardDrawn += OnCardDrawn;
        _decoder.GameResultReceived += OnGameResult;
        _decoder.CuttingCardDrawn += OnCutCard;
        _decoder.ErrorOccurred += value =>
        {
            lock (_sync)
            {
                TrackSequence(value.Seq);
                _error = $"{value.ErrorCode}: {value.ErrorMessage}";
            }
            Publish();
        };
        _decoder.ErrorCleared += (_, _) =>
        {
            lock (_sync)
            {
                _error = string.Empty;
            }
            Publish();
        };
        _decoder.Diagnostic += (kind, message) =>
        {
            lock (_sync)
            {
                AddDiagnostic(kind, message);
            }
            Publish();
        };
        _decoder.StatusChanged += message =>
        {
            lock (_sync)
            {
                _statusMessage = message;
            }
            Publish();
        };
    }

    public MoxaMonitorEndpoint Endpoint { get; }
    public event Action<MoxaMonitorSnapshot>? SnapshotChanged;
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public MoxaMonitorSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshot();
            }
        }
    }

    public Task StartAsync()
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _sessionCts = new CancellationTokenSource();
            _epoch++;
            _startedAt = DateTimeOffset.UtcNow;
            ResetEvidence();
            var epoch = _epoch;
            _runTask = RunAsync(epoch, _sessionCts.Token);
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync()
    {
        Task? runTask;
        IMoxaReceiveTransport? transport;
        lock (_sync)
        {
            _sessionCts?.Cancel();
            runTask = _runTask;
            transport = _transport;
        }

        if (transport is not null)
        {
            await transport.StopAsync();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_sync)
        {
            _state = MoxaMonitorConnectionState.Stopped;
            _statusMessage = "已停止";
            _transport = null;
            _runTask = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
        Publish();
    }

    private async Task RunAsync(long epoch, CancellationToken cancellationToken)
    {
        var retry = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IMoxaReceiveTransport? transport = null;
            try
            {
                SetState(epoch, MoxaMonitorConnectionState.Connecting, "連線中");
                transport = _transportFactory();
                lock (_sync)
                {
                    if (epoch != _epoch)
                    {
                        return;
                    }
                    _transport = transport;
                }
                await transport.ConnectAsync(Endpoint.Host, Endpoint.Port, cancellationToken);
                retry = 0;
                SetState(epoch, MoxaMonitorConnectionState.Live, "即時接收中");

                var buffer = new byte[512];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await transport.ReceiveAsync(buffer, cancellationToken);
                    if (count <= 0)
                    {
                        throw new IOException("MOXA closed the connection.");
                    }

                    lock (_sync)
                    {
                        if (epoch != _epoch)
                        {
                            return;
                        }
                        _lastFrameAt = DateTimeOffset.UtcNow;
                        AddDiagnostic("RXRAW", ToHex(buffer.AsSpan(0, count)));
                    }
                    _decoder.Feed(buffer.AsSpan(0, count));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (epoch != _epoch)
                    {
                        return;
                    }
                    _partial = true;
                    _decoder.Reset();
                    _lastSequence = null;
                    AddDiagnostic("SYS", exception.Message);
                }

                if (retry >= _retryDelays.Count)
                {
                    SetState(epoch, MoxaMonitorConnectionState.Failed, exception.Message);
                    break;
                }

                var delay = _retryDelays[retry++];
                SetState(
                    epoch,
                    MoxaMonitorConnectionState.Backoff,
                    $"連線失敗，{delay.TotalSeconds:0} 秒後重試");
                await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                if (transport is not null)
                {
                    await transport.DisposeAsync();
                }
                lock (_sync)
                {
                    if (ReferenceEquals(_transport, transport))
                    {
                        _transport = null;
                    }
                }
            }
        }
    }

    private void OnCardDrawn(SerialListener.CardInfo card)
    {
        lock (_sync)
        {
            TrackSequence(card.Seq);
            var formatted = $"{card.Value}{SuitSymbol(card.Suit)}";
            if (card.Target == "Player")
            {
                _playerCards.Add(formatted);
            }
            else if (card.Target == "Banker")
            {
                _bankerCards.Add(formatted);
            }
        }
        Publish();
    }

    private void OnGameResult(SerialListener.GameResult result)
    {
        lock (_sync)
        {
            TrackSequence(result.Seq);
            _gameResult = string.IsNullOrEmpty(result.Pair)
                ? result.Result
                : $"{result.Result} / {result.Pair}";
            _gameResultCount++;
            _playerCards.Clear();
            _bankerCards.Clear();
        }
        Publish();
    }

    private void OnCutCard(SerialListener.CutCardInfo cutCard)
    {
        lock (_sync)
        {
            TrackSequence(cutCard.Seq);
            _cutCardCount++;
            _playerCards.Clear();
            _bankerCards.Clear();
            _gameResult = string.Empty;
            _error = string.Empty;
            _partial = false;
        }
        Publish();
    }

    private void TrackSequence(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
        {
            _partial = true;
            return;
        }

        var current = (byte)sequence[0];
        if (_lastSequence.HasValue)
        {
            var expected = _lastSequence.Value == (byte)'9'
                ? (byte)'0'
                : (byte)(_lastSequence.Value + 1);
            if (current != expected)
            {
                _partial = true;
                AddDiagnostic(
                    "SYS",
                    $"Sequence gap: expected {(char)expected}, received {(char)current}");
            }
        }
        _lastSequence = current;
    }

    private void ResetEvidence()
    {
        _decoder.Reset();
        _lastFrameAt = null;
        _state = MoxaMonitorConnectionState.Connecting;
        _partial = true;
        _playerCards.Clear();
        _bankerCards.Clear();
        _gameResult = string.Empty;
        _gameResultCount = 0;
        _cutCardCount = 0;
        _error = string.Empty;
        _lastSequence = null;
        _diagnostics.Clear();
        _dropped = 0;
        _statusMessage = "連線中";
    }

    private void SetState(
        long epoch,
        MoxaMonitorConnectionState state,
        string message)
    {
        lock (_sync)
        {
            if (epoch != _epoch)
            {
                return;
            }
            _state = state;
            _statusMessage = message;
        }
        Publish();
    }

    private void AddDiagnostic(string kind, string message)
    {
        if (_diagnostics.Count == DiagnosticCapacity)
        {
            _diagnostics.Dequeue();
            _dropped++;
        }
        _diagnostics.Enqueue(new MoxaMonitorDiagnostic(DateTimeOffset.UtcNow, kind, message));
    }

    private MoxaMonitorSnapshot BuildSnapshot() =>
        new(
            Endpoint,
            _epoch,
            _state,
            _startedAt,
            _lastFrameAt,
            _partial,
            _playerCards.ToArray(),
            _bankerCards.ToArray(),
            _gameResult,
            _gameResultCount,
            _cutCardCount,
            _error,
            _dropped,
            _diagnostics.ToArray(),
            _statusMessage);

    private void Publish()
    {
        MoxaMonitorSnapshot snapshot;
        lock (_sync)
        {
            snapshot = BuildSnapshot();
        }
        SnapshotChanged?.Invoke(snapshot);
    }

    private static string SuitSymbol(string suit) => suit switch
    {
        "Spade" => "♠",
        "Heart" => "♥",
        "Diamond" => "♦",
        "Club" => "♣",
        _ => string.Empty
    };

    private static string ToHex(ReadOnlySpan<byte> bytes) =>
        string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2")));

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class MoxaMonitorManager : IAsyncDisposable
{
    private readonly Dictionary<string, MoxaMonitorSession> _sessions;

    public MoxaMonitorManager(
        Func<MoxaMonitorEndpoint, IMoxaReceiveTransport>? transportFactory = null)
    {
        _sessions = MoxaMonitorCatalog.Endpoints.ToDictionary(
            endpoint => endpoint.Desk,
            endpoint => new MoxaMonitorSession(
                endpoint,
                transportFactory is null ? null : () => transportFactory(endpoint)),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<MoxaMonitorSession> Sessions => _sessions.Values;

    public MoxaMonitorSession Get(string desk) =>
        _sessions.TryGetValue(desk, out var session)
            ? session
            : throw new ArgumentOutOfRangeException(nameof(desk), desk, "Unknown MOXA desk.");

    public Task StartAsync(string desk) => Get(desk).StartAsync();
    public Task StopAsync(string desk) => Get(desk).StopAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
    }
}
