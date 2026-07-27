namespace SirkAgent.Service.Core;

internal enum ModuleHealthStatus
{
    Healthy,
    Warning,
    Critical
}

internal sealed record ModuleHealthSnapshot(
    string Module,
    ModuleHealthStatus Status,
    string Code,
    string Summary,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    string? Error,
    IReadOnlyDictionary<string, string?> Details);

internal sealed class ModuleHealthRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ModuleHealthSnapshot> _modules = new(StringComparer.OrdinalIgnoreCase);

    public void Report(ModuleHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Module);

        lock (_sync)
            _modules[snapshot.Module] = snapshot;
    }

    public IReadOnlyList<ModuleHealthSnapshot> Snapshot()
    {
        lock (_sync)
            return _modules.Values.OrderBy(module => module.Module, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ModuleHealthStatus OverallStatus()
    {
        var modules = Snapshot();
        if (modules.Any(module => module.Status == ModuleHealthStatus.Critical))
            return ModuleHealthStatus.Critical;
        if (modules.Any(module => module.Status == ModuleHealthStatus.Warning))
            return ModuleHealthStatus.Warning;
        return ModuleHealthStatus.Healthy;
    }
}
