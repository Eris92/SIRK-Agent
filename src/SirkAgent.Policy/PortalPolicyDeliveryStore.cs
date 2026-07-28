using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SirkAgent.Policy;

public sealed class PortalPolicyDeliveryStore
{
    private readonly string _inboxDirectory;
    private readonly JsonSerializerOptions _json;

    public PortalPolicyDeliveryStore(string inboxDirectory, JsonSerializerOptions json)
    {
        _inboxDirectory = Path.GetFullPath(inboxDirectory);
        _json = json;
    }

    public int Store(string tenantId, string deviceId, IEnumerable<JsonElement>? policies)
    {
        var stored = 0;
        Directory.CreateDirectory(_inboxDirectory);
        foreach (var raw in policies?.Take(20) ?? [])
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(raw, _json);
            if (bytes.Length > 256 * 1024)
                continue;
            var policy = JsonSerializer.Deserialize<PolicyEnvelope>(bytes, _json);
            if (policy is null ||
                !string.Equals(policy.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(policy.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(policy.PolicyId) ||
                string.IsNullOrWhiteSpace(policy.Signature?.Value))
                continue;

            var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy.PolicyId))) +
                           ".policy.json";
            var target = Path.Combine(_inboxDirectory, fileName);
            if (File.Exists(target))
                continue;
            var temporary = target + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, overwrite: false);
                stored++;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return stored;
    }
}
