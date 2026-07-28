using SirkAgent.Service;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class ActivityCollectionPolicyTests
{
    [Fact]
    public void Detailed_collection_requires_case_mode_and_valid_expiry()
    {
        var valid = new ActivityCollectionPolicy(true, true, true, true, true, true, true, true, true, [],
            60, "Investigation", "CASE-42", DateTimeOffset.UtcNow.AddHours(1));
        var normal = valid with { Mode = "Normal" };
        var missingCase = valid with { CaseId = null };
        var expired = valid with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };

        Assert.True(valid.InvestigationAuthorized);
        Assert.False(normal.InvestigationAuthorized);
        Assert.False(missingCase.InvestigationAuthorized);
        Assert.False(expired.InvestigationAuthorized);
    }

    [Fact]
    public void Reads_activity_scope_from_signed_active_policy_shape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = Path.Combine(directory, "evidence");
            Directory.CreateDirectory(root);
            var path = Path.Combine(directory, "active-policy.json");
            File.WriteAllText(path, $$"""
                {
                  "mode": "Investigation",
                  "caseId": "CASE-100",
                  "expiresAtUtc": "{{DateTimeOffset.UtcNow.AddHours(2):O}}",
                  "settings": {
                    "activityCollection": {
                      "enabled": true,
                      "processes": true,
                      "interactiveContext": true,
                      "clipboardMetadata": true,
                      "usb": true,
                      "printing": true,
                      "fileRoots": ["{{root.Replace("\\", "\\\\")}}"],
                      "intervalSeconds": 60
                    }
                  }
                }
                """);

            var result = ActivityCollectorWorker.ReadPolicy(path);

            Assert.True(result.Enabled);
            Assert.True(result.InvestigationAuthorized);
            Assert.True(result.CollectClipboardMetadata);
            Assert.Single(result.FileRoots);
            Assert.Equal(60, result.IntervalSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_or_invalid_policy_disables_collection()
    {
        var result = ActivityCollectorWorker.ReadPolicy(Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString("N"), "missing.json"));
        Assert.False(result.Enabled);
        Assert.False(result.InvestigationAuthorized);
    }
}
