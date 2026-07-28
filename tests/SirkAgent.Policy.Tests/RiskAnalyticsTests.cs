using System.Text.Json;
using SirkAgent.Service;
using SirkAgent.Service.Core;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class RiskAnalyticsTests
{
    [Fact]
    public void Correlates_mass_download_archive_and_upload()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            Event(1, now.AddMinutes(-3), "Browser", "Activity", new
            {
                caseId = "CASE-7",
                browserEvent = new { type = "download", bytes = 700_000_000L }
            }),
            Event(2, now.AddMinutes(-2), "Browser", "Activity", new
            {
                caseId = "CASE-7",
                browserEvent = new
                {
                    type = "uploadSelection",
                    files = new[] { new { name = "company-data.zip", bytes = 700_000_000L } }
                }
            }),
            Event(3, now.AddMinutes(-1), "Browser", "Activity", new
            {
                caseId = "CASE-7",
                browserEvent = new { type = "uploadResult", bytes = 700_000_000L, ok = true }
            })
        };
        var policy = new RiskAnalyticsPolicy(true, "InsiderRisk", "CASE-7",
            now.AddHours(1), 60, 20, 500_000_000, true, 80);

        var report = RiskAnalyticsWorker.Evaluate(policy, "DEVICE-1", events,
            new RiskBaseline(10, 5, null));

        Assert.Equal("Critical", report.Severity);
        Assert.Contains(report.Findings, value => value.Code == "MASS_DOWNLOAD");
        Assert.Contains(report.Findings, value => value.Code == "ARCHIVE_TO_UPLOAD");
        Assert.Contains(report.Findings, value => value.Code == "BASELINE_DEVIATION");
    }

    [Fact]
    public void Reads_bounded_insider_risk_policy()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "active-policy.json");
            File.WriteAllText(path, $$"""
              {
                "mode": "InsiderRisk",
                "caseId": "CASE-8",
                "expiresAtUtc": "{{DateTimeOffset.UtcNow.AddHours(1):O}}",
                "settings": {
                  "riskAnalytics": {
                    "enabled": true,
                    "windowMinutes": 99999,
                    "massDownloadCount": 1,
                    "massDownloadBytes": 0
                  }
                }
              }
              """);

            var result = RiskAnalyticsWorker.ReadPolicy(path);

            Assert.True(result.Enabled);
            Assert.Equal(10080, result.WindowMinutes);
            Assert.Equal(2, result.MassDownloadCount);
            Assert.Equal(1, result.MassDownloadBytes);
        }
        finally { directory.Delete(true); }
    }

    private static EvidenceEvent Event(long sequence, DateTimeOffset timestamp, string category,
        string action, object data) =>
        new(Guid.NewGuid(), timestamp, sequence, "investa", "DEVICE-1", category, action,
            JsonSerializer.SerializeToElement(data), sequence == 1 ? null : $"hash-{sequence - 1}",
            $"hash-{sequence}");
}
