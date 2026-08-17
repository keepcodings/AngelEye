using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerShutdownSignalTests
{
    [Fact]
    public void Request_CancelsToken_AndRemainsSafeAfterDispose()
    {
        WorkerShutdownSignal signal = new();

        signal.Request();

        Assert.True(signal.Token.IsCancellationRequested);
        signal.Dispose();
        signal.Request();
        signal.Dispose();
    }
}
