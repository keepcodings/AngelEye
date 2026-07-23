using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerSettingsIdentityTests
{
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
