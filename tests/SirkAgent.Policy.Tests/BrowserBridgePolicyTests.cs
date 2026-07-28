using SirkAgent.Service;
using System.Text.Json;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class BrowserBridgePolicyTests
{
    [Fact]
    public void Accepts_only_policy_allowed_events_and_domains()
    {
        var policy = new BrowserBridgePolicy(true, ["example.com"],
            new HashSet<string>(["navigation", "download"]), "Investigation", "CASE-9",
            DateTimeOffset.UtcNow.AddHours(1));

        using var allowed = JsonDocument.Parse(
            """{"type":"navigation","url":"https://portal.example.com/path"}""");
        using var deniedDomain = JsonDocument.Parse(
            """{"type":"navigation","url":"https://example.org/path"}""");
        using var deniedType = JsonDocument.Parse(
            """{"type":"formSubmit","url":"https://example.com/path"}""");

        Assert.True(BrowserBridgeWorker.Evaluate(policy, allowed.RootElement).Accepted);
        Assert.Equal("BROWSER_DOMAIN_NOT_ALLOWED",
            BrowserBridgeWorker.Evaluate(policy, deniedDomain.RootElement).Code);
        Assert.Equal("BROWSER_EVENT_NOT_ALLOWED",
            BrowserBridgeWorker.Evaluate(policy, deniedType.RootElement).Code);
    }

    [Theory]
    [InlineData("Normal", "CASE-1")]
    [InlineData("Investigation", null)]
    [InlineData("InsiderRisk", "")]
    public void Rejects_without_valid_investigation_authorization(string mode, string? caseId)
    {
        var policy = new BrowserBridgePolicy(true, ["example.com"],
            new HashSet<string>(["tab"]), mode, caseId, DateTimeOffset.UtcNow.AddHours(1));
        using var message = JsonDocument.Parse("""{"type":"tab","url":"https://example.com"}""");
        Assert.Equal("BROWSER_POLICY_NOT_AUTHORIZED",
            BrowserBridgeWorker.Evaluate(policy, message.RootElement).Code);
    }

    [Fact]
    public void Reads_bridge_scope_from_active_policy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-browser-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "active-policy.json");
            File.WriteAllText(path, $$"""
                {
                  "mode": "InsiderRisk",
                  "caseId": "CASE-22",
                  "expiresAtUtc": "{{DateTimeOffset.UtcNow.AddHours(2):O}}",
                  "settings": {
                    "browserBridge": {
                      "enabled": true,
                      "allowedDomains": ["Example.COM", "upload.example.net"],
                      "allowedEvents": ["tab", "download", "unknown"]
                    }
                  }
                }
                """);
            var policy = BrowserBridgeWorker.ReadPolicy(path);
            Assert.True(policy.Authorized);
            Assert.Equal(2, policy.AllowedDomains.Count);
            Assert.Equal(2, policy.AllowedEvents.Count);
            Assert.DoesNotContain("unknown", policy.AllowedEvents);
        }
        finally { Directory.Delete(directory, true); }
    }
}
