using System.Text.Json;
using SirkAgent.Service;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class RemoteCommandExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TerminalIsDeniedWithoutSignedPolicyFlag()
    {
        var executor = new RemoteCommandExecutor(Json);
        var command = Command("terminal.execute", new { command = "Write-Output unsafe" });

        var result = await executor.ExecuteAsync(command, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("OPERATION_NOT_ALLOWED", result.Code);
    }

    [Fact]
    public async Task TerminalRunsWhenPolicyExplicitlyEnablesIt()
    {
        var executor = new RemoteCommandExecutor(Json);
        var command = Command("terminal.execute", new { command = "Write-Output SIRK_REMOTE_OK" });
        var policy = JsonSerializer.SerializeToElement(new
        {
            settings = new { remoteTerminalEnabled = true }
        }, Json);

        var result = await executor.ExecuteAsync(command, policy, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("TERMINAL_OK", result.Code);
        Assert.Contains("SIRK_REMOTE_OK", result.Output);
    }

    [Fact]
    public async Task FilesListReturnsDirectoryEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "sirk-agent-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "proof.txt"), "ok");
            var executor = new RemoteCommandExecutor(Json);
            var command = Command("files.list", new { path = root });
            var policy = JsonSerializer.SerializeToElement(new
            {
                settings = new { remoteFilesEnabled = true }
            }, Json);

            var result = await executor.ExecuteAsync(command, policy, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Contains(result.Data!.Value.EnumerateArray(),
                item => item.GetProperty("name").GetString() == "proof.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSessionsRequiresPolicyAndReturnsACollection()
    {
        var executor = new RemoteCommandExecutor(Json);
        var command = Command("desktop.sessions", new { });

        var denied = await executor.ExecuteAsync(command, null, CancellationToken.None);
        Assert.False(denied.Ok);
        Assert.Equal("OPERATION_NOT_ALLOWED", denied.Code);

        var policy = JsonSerializer.SerializeToElement(new
        {
            settings = new { remoteDesktopEnabled = true }
        }, Json);
        var allowed = await executor.ExecuteAsync(command, policy, CancellationToken.None);

        Assert.True(allowed.Ok);
        Assert.Equal("DESKTOP_SESSIONS_OK", allowed.Code);
        Assert.Equal(JsonValueKind.Array, allowed.Data!.Value.ValueKind);
    }

    [Fact]
    public void AdministrativeDesktopTools_AreRestrictedToTheBuiltInAllowlist()
    {
        Assert.EndsWith("powershell.exe",
            InteractiveAdminLauncher.ResolveTool("powershell").Application,
            StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => InteractiveAdminLauncher.ResolveTool("C:\\temp\\tool.exe"));
    }

    private static PortalRemoteCommand Command(string type, object parameters) =>
        new(Guid.NewGuid().ToString("N"), type, JsonSerializer.SerializeToElement(parameters, Json),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
}
