using SirkAgent.Service.Core;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class ManagementPlaneHealthTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 3)]
    public void Classifies_directory_membership(bool domain, bool entra, int expected)
    {
        Assert.Equal((HostDirectoryKind)expected, ManagementPlaneHealth.Classify(domain, entra));
    }

    [Fact]
    public async Task Reports_hybrid_host_and_policy_checks()
    {
        var probe = new FakeProbe(new Dictionary<string, CommandResult>
        {
            ["dsregcmd.exe /status"] = new(0, """
                AzureAdJoined : YES
                EnterpriseJoined : NO
                DomainJoined : YES
                DomainName : CONTOSO
                DeviceId : 11111111-1111-1111-1111-111111111111
                TenantId : 22222222-2222-2222-2222-222222222222
                """, ""),
            ["nltest.exe /sc_verify:CONTOSO"] = new(0, "trusted", ""),
            ["gpresult.exe /scope computer /r"] = new(0, "Applied Group Policy Objects", "")
        });

        var result = await new ManagementPlaneHealth(probe).InspectAsync(
            ManagementPlaneRequirements.Default with { RequireDefender = false, RequireFirewall = false },
            CancellationToken.None);

        Assert.Equal(HostDirectoryKind.Hybrid, result.DirectoryKind);
        Assert.True(result.DomainJoined);
        Assert.True(result.AzureAdJoined);
        Assert.Contains(result.Checks, check => check.Id == "ad-secure-channel" &&
                                                check.Status == ManagementCheckStatus.Healthy);
        Assert.Contains(result.Checks, check => check.Id == "entra-central-policy" &&
                                                check.Status == ManagementCheckStatus.NotConfigured);
        Assert.False(result.Healthy);
    }

    [Fact]
    public async Task Safe_repair_is_explicit_and_recorded()
    {
        var probe = new FakeProbe(new Dictionary<string, CommandResult>
        {
            ["dsregcmd.exe /status"] = new(0, "DomainJoined : YES\nDomainName : CONTOSO", ""),
            ["nltest.exe /sc_verify:CONTOSO"] = new(1, "", "broken"),
            ["nltest.exe /sc_reset:CONTOSO"] = new(0, "reset", ""),
            ["gpresult.exe /scope computer /r"] = new(0, "ok", "")
        });

        var result = await new ManagementPlaneHealth(probe).InspectAsync(
            ManagementPlaneRequirements.Default with
            {
                AllowSafeRepair = true,
                RequireDefender = false,
                RequireFirewall = false
            }, CancellationToken.None);
        var channel = Assert.Single(result.Checks, check => check.Id == "ad-secure-channel");
        Assert.True(channel.RepairAttempted);
        Assert.True(channel.RepairSucceeded);
        Assert.Equal(ManagementCheckStatus.Healthy, channel.Status);
    }

    [Fact]
    public async Task Windows_host_produces_a_real_management_plane_snapshot()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await new ManagementPlaneHealth(new ProcessCommandProbe())
            .InspectAsync(ManagementPlaneRequirements.Default, CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Id == "directory-registration");
        Assert.Contains(result.Checks, check => check.Id == "baseline-firewall");
        Assert.Contains(result.Checks, check => check.Id == "baseline-defender");
        Assert.NotEqual(default, result.TimestampUtc);
    }

    private sealed class FakeProbe(IReadOnlyDictionary<string, CommandResult> results) : ICommandProbe
    {
        public Task<CommandResult> RunAsync(string fileName, string arguments, TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(results.TryGetValue(fileName + " " + arguments, out var result)
                ? result : new CommandResult(1, "", "missing fake"));
    }
}
