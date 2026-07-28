using System.Text.Json;
using SirkAgent.Service;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class ManagementPlanePolicyTests
{
    [Fact]
    public void Reads_requirements_only_from_active_policy_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-plane-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "active-policy.json");
            File.WriteAllText(path, """
                {
                  "settings": {
                    "managementPlane": {
                      "requiredAppliedGpos": ["SIRK Security Baseline", "SIRK Defender"],
                      "requireDefender": true,
                      "requireFirewall": true,
                      "requireBitLocker": true,
                      "requireSecureBoot": true,
                      "requireTpm": true,
                      "allowSafeRepair": false
                    }
                  }
                }
                """);

            var result = ManagementPlaneWorker.ReadRequirements(path,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.Equal(2, result.RequiredAppliedGpos.Count);
            Assert.True(result.RequireBitLocker);
            Assert.True(result.RequireSecureBoot);
            Assert.True(result.RequireTpm);
            Assert.False(result.AllowSafeRepair);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
