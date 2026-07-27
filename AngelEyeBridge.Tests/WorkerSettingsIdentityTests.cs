using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerSettingsIdentityTests
{
    [Fact]
    public void BridgeDefaults_DoNotStartRoundFromTransportConnection()
    {
        BridgeWorkerSettings settings = new();

        Assert.False(settings.AutoStartRoundOnConnect);
        Assert.False(settings.AutoStartNextRoundAfterResult);
    }

    [Fact]
    public void Validate_RejectsLegacyJwtSigningMode_WhenBmsTransmissionIsEnabled()
    {
        WorkerSettings settings = ValidSettings();
        settings.Shoes[0].BmsTransmitEnabled = true;
        settings.Bms.AutoGenerateJwt = true;
        settings.Bms.JwtSigningKey = "REPLACE_WITH_QA_SOURCE_PROVIDER_SIGNING_KEY";
        settings.Normalize(Path.GetTempPath());

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => settings.Validate());

        Assert.Contains("JwtSigningKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsConfiguredSigningKey_EvenWhenAutoGenerateIsFalse()
    {
        WorkerSettings settings = ValidSettings();
        settings.Shoes[0].BmsTransmitEnabled = true;
        settings.Bms.AutoGenerateJwt = false;
        settings.Bms.JwtSigningKey = "legacy-shared-signing-key";
        settings.Bms.ClientId = "angel-qa-29";
        settings.Bms.ClientSecret = "client-secret";
        settings.Normalize(Path.GetTempPath());

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => settings.Validate());

        Assert.Contains("JwtSigningKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RequiresClientCredentials_WhenBmsTransmissionIsEnabled()
    {
        WorkerSettings settings = ValidSettings();
        settings.Shoes[0].BmsTransmitEnabled = true;
        settings.Bms.AutoGenerateJwt = false;
        settings.Bms.JwtSigningKey = string.Empty;
        settings.Bms.ClientId = string.Empty;
        settings.Bms.ClientSecret = string.Empty;
        settings.Normalize(Path.GetTempPath());

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => settings.Validate());

        Assert.Contains("ClientId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ClientSecret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsLegacyFixedToken_WhenBmsTransmissionIsEnabled()
    {
        WorkerSettings settings = ValidSettings();
        settings.Shoes[0].BmsTransmitEnabled = true;
        settings.Bms.Token = "legacy-fixed-token";
        settings.Bms.ClientId = "angel-qa-29";
        settings.Bms.ClientSecret = "client-secret";
        settings.Normalize(Path.GetTempPath());

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => settings.Validate());

        Assert.Contains("Token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsClientCredentials_WhenBmsTransmissionIsEnabled()
    {
        WorkerSettings settings = ValidSettings();
        settings.Shoes[0].BmsTransmitEnabled = true;
        settings.Bms.AutoGenerateJwt = false;
        settings.Bms.JwtSigningKey = string.Empty;
        settings.Bms.ClientId = "angel-qa-29";
        settings.Bms.ClientSecret = "client-secret";
        settings.Normalize(Path.GetTempPath());

        settings.Validate();
    }

    [Theory]
    [InlineData("http://bms.test/api/source/angel/events")]
    [InlineData("bms.test/api/source/angel/events")]
    public void Validate_RejectsNonHttpsBmsUrl(string eventApiUrl)
    {
        WorkerSettings settings = ValidSettings();
        settings.Bms.EventApiUrl = eventApiUrl;
        settings.Normalize(Path.GetTempPath());

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => settings.Validate());

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllowsMissingBmsSecret_WhenEveryEndpointIsLocalOnly()
    {
        WorkerSettings settings = ValidSettings();
        settings.Shoes[0].BmsTransmitEnabled = false;
        settings.Bms.AutoGenerateJwt = true;
        settings.Bms.JwtSigningKey = string.Empty;
        settings.Normalize(Path.GetTempPath());

        settings.Validate();
    }

    [Theory]
    [InlineData("", "QA", "Primary")]
    [InlineData("telebet-29", "", "Primary")]
    [InlineData("telebet-29", "QA", "")]
    public void Validate_RejectsMissingSourceIdentity(string instanceName, string environment, string role)
    {
        WorkerSettings settings = ValidSettings();
        settings.Bridge.InstanceName = instanceName;
        settings.Bridge.EnvironmentName = environment;
        settings.Bridge.Role = role;
        settings.Normalize(Path.GetTempPath());

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_AcceptsCompleteSourceIdentity()
    {
        WorkerSettings settings = ValidSettings();
        settings.Normalize(Path.GetTempPath());

        settings.Validate();

        Assert.Equal("telebet-29", settings.Bridge.InstanceName);
        Assert.Equal("QA", settings.Bridge.EnvironmentName);
        Assert.Equal("Primary", settings.Bridge.Role);
    }

    [Fact]
    public void HealthListener_DefaultsToLoopback_AndAllowsExplicitOverride()
    {
        HealthWorkerSettings health = new();
        health.Normalize();
        Assert.Equal("127.0.0.1", health.Host);

        health.Host = "10.5.32.29";
        health.Normalize();
        Assert.Equal("10.5.32.29", health.Host);
    }

    private static WorkerSettings ValidSettings() => new()
    {
        Bridge = new BridgeWorkerSettings
        {
            InstanceName = "telebet-29",
            EnvironmentName = "QA",
            Role = "Primary",
            ConnectionMode = ShoeConnectionMode.MoxaTcp
        },
        Shoes =
        [
            new ShoeEndpointSettings
            {
                Enabled = true,
                DeskName = "901桌",
                SourceDataCode = "901",
                ShoeId = "SHOE901",
                MoxaHost = "10.5.32.24",
                MoxaPort = 4001
            }
        ]
    };
}
