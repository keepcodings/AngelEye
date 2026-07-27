using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeDiagnosticFormatterTests
{
    [Fact]
    public void ExceptionDiagnostic_IncludesTypeAndStackWithoutSecrets()
    {
        Exception exception;
        try
        {
            ThrowWithSecret();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        string diagnostic = BridgeDiagnosticFormatter.FormatException(exception);

        Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowWithSecret), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", diagnostic, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("event-123", "event-123")]
    [InlineData("command:1.2_3", "command:1.2_3")]
    [InlineData("contains space", "fallback")]
    [InlineData("../invalid", "fallback")]
    public void CorrelationId_IsBoundedToSafeHeaderCharacters(
        string candidate,
        string expected)
    {
        Assert.Equal(
            expected,
            BridgeDiagnosticFormatter.NormalizeCorrelationId(
                candidate,
                "fallback"));
    }

    private static void ThrowWithSecret()
    {
        throw new InvalidOperationException(
            "clientSecret=super-secret Authorization: Bearer jwt-token");
    }
}
