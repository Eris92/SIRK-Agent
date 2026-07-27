using System.Text.Json;

namespace SirkAgent.Service.Core;

internal sealed record SecurityRuntimeSnapshot(
    SecurityStateSnapshot Security,
    string OverallHealth,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ModuleHealthSnapshot> Modules);

internal sealed class HealthMonitor
{
    private readonly string _path;
    private readonly ModuleHealthRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;

    public HealthMonitor(
        string path,
        ModuleHealthRegistry registry,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public SecurityRuntimeSnapshot Capture(SecurityStateSnapshot securityState)
    {
        ArgumentNullException.ThrowIfNull(securityState);

        var snapshot = new SecurityRuntimeSnapshot(
            securityState,
            _registry.OverallStatus().ToString(),
            DateTimeOffset.UtcNow,
            _registry.Snapshot());

        AtomicFile.WriteJson(_path, snapshot, _jsonOptions);
        return snapshot;
    }
}
