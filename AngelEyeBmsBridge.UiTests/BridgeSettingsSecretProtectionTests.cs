using System.Text.Json;
using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBmsBridge.UiTests;

public sealed class BridgeSettingsSecretProtectionTests
{
    [Fact]
    public void ClientSecret_IsDpapiProtectedAndExcludedFromPersistedJson()
    {
        const string Secret = "qa-bridge-client-secret-value";
        string protectedValue = BridgeSettings.ProtectBmsClientSecret(Secret);
        BridgeSettings settings = new()
        {
            BmsClientSecret = Secret,
            BmsClientSecretProtected = protectedValue
        };

        string json = JsonSerializer.Serialize(settings);

        Assert.NotEmpty(protectedValue);
        Assert.NotEqual(Secret, protectedValue);
        Assert.Equal(
            Secret,
            BridgeSettings.UnprotectBmsClientSecret(protectedValue));
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"BmsClientSecret\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"BmsClientSecretProtected\"",
            json,
            StringComparison.Ordinal);
    }
}
