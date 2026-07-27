using SirkAgent.Policy;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PolicyHeartbeatFactoryTests
{
    [Theory]
    [InlineData("OK")]
    [InlineData("STATE_INITIALIZED")]
    public void Create_HealthyStateCodesDoNotReportTamper(string stateCode)
    {
        var heartbeat = PolicyHeartbeatFactory.Create(
            PolicyState.Empty,
            "tenant",
            "device",
            DateTimeOffset.UtcNow,
            stateCode,
            "Startup");

        Assert.False(heartbeat.TamperDetected);
        Assert.Null(heartbeat.TamperReason);
        Assert.Equal(stateCode, heartbeat.StateStatus);
    }

    [Fact]
    public void Create_IntegrityFailureReportsTamper()
    {
        var heartbeat = PolicyHeartbeatFactory.Create(
            PolicyState.Empty,
            "tenant",
            "device",
            DateTimeOffset.UtcNow,
            "STATE_UNPROTECT_FAILED",
            "Startup");

        Assert.True(heartbeat.TamperDetected);
        Assert.Equal("STATE_UNPROTECT_FAILED", heartbeat.TamperReason);
    }
}
