using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed class ManagementPlaneWorker : BackgroundService
{
    private const string TenantId = "investa";
    private readonly ILogger<ManagementPlaneWorker> _logger;

    public ManagementPlaneWorker(ILogger<ManagementPlaneWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = AgentPaths.CreateDefault();
        paths.EnsureDirectories();
        var reportPath = Path.Combine(paths.AgentDirectory, "management-plane-health.json");
        var activePolicyPath = Path.Combine(paths.AgentDirectory, "active-policy.json");
        var inspector = new ManagementPlaneHealth(new ProcessCommandProbe());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var protector = new DpapiMachineStateProtector();
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, protector).LoadOrCreate(TenantId);
        var telemetry = new TelemetryQueue(paths.TelemetryQueueDirectory, protector,
            50L * 1024 * 1024, options);
        var evidence = new EvidenceChain(paths.EvidenceLogPath, paths.EvidenceStatePath, protector, options);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var requirements = ReadRequirements(activePolicyPath, options);
                var snapshot = await inspector.InspectAsync(requirements, stoppingToken);
                AtomicFile.WriteJson(reportPath, snapshot, options);
                var priority = snapshot.Healthy ? TelemetryPriority.Normal : TelemetryPriority.High;
                telemetry.Enqueue("ManagementPlane", "HealthEvaluated", priority, snapshot);
                evidence.Append(TenantId, identity.DeviceId, "ManagementPlane", "HealthEvaluated", snapshot);
                _logger.Log(snapshot.Healthy ? LogLevel.Information : LogLevel.Warning,
                    "Management plane health inspected. Kind={Kind} Healthy={Healthy}",
                    snapshot.DirectoryKind, snapshot.Healthy);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Management plane health inspection failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    internal static ManagementPlaneRequirements ReadRequirements(string activePolicyPath,
        JsonSerializerOptions options)
    {
        if (!File.Exists(activePolicyPath))
            return ManagementPlaneRequirements.Default;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(activePolicyPath));
            if (!document.RootElement.TryGetProperty("settings", out var settings) ||
                !settings.TryGetProperty("managementPlane", out var plane) ||
                plane.ValueKind != JsonValueKind.Object)
                return ManagementPlaneRequirements.Default;

            bool Flag(string name, bool fallback) =>
                plane.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean() : fallback;
            var gpos = plane.TryGetProperty("requiredAppliedGpos", out var required) &&
                       required.ValueKind == JsonValueKind.Array
                ? required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray()
                : [];
            EntraPolicySnapshot? entra = null;
            if (plane.TryGetProperty("entraPolicySnapshot", out var snapshot) &&
                snapshot.ValueKind == JsonValueKind.Object)
                entra = JsonSerializer.Deserialize<EntraPolicySnapshot>(snapshot, options);

            return new ManagementPlaneRequirements(gpos,
                Flag("requireDefender", true), Flag("requireFirewall", true),
                Flag("requireBitLocker", true), Flag("requireSecureBoot", true),
                Flag("requireTpm", true), Flag("allowSafeRepair", false), entra);
        }
        catch
        {
            return ManagementPlaneRequirements.Default;
        }
    }
}
