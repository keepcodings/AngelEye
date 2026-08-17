namespace AngelEyeBmsBridge;

/// <summary>
/// Owns process shutdown subscriptions and keeps late or repeated shutdown
/// notifications from touching a disposed cancellation source.
/// </summary>
internal sealed class WorkerShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private int _disposed;

    public WorkerShutdownSignal()
    {
        Console.CancelKeyPress += HandleCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
    }

    public CancellationToken Token => _source.Token;

    internal void Request()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Dispose may race a final process notification. Shutdown is already complete.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Console.CancelKeyPress -= HandleCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= HandleProcessExit;
        _source.Dispose();
    }

    private void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Request();
    }

    private void HandleProcessExit(object? sender, EventArgs eventArgs) => Request();
}
