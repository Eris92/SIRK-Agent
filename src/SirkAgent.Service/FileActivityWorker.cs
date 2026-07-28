using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed record FileFingerprint(long Length, DateTime LastWriteUtc, string? Sha256);
internal sealed record FileChange(string Action, string Path, long Length, string? Sha256);

internal sealed class FileActivityWorker : BackgroundService
{
    private const string TenantId = "investa";
    private const int MaximumFiles = 20_000;
    private const long MaximumHashBytes = 100L * 1024 * 1024;
    private readonly ILogger<FileActivityWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public FileActivityWorker(ILogger<FileActivityWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = AgentPaths.CreateDefault();
        paths.EnsureDirectories();
        var policyPath = Path.Combine(paths.AgentDirectory, "active-policy.json");
        var protector = new DpapiMachineStateProtector();
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, protector).LoadOrCreate(TenantId);
        var telemetry = new TelemetryQueue(paths.TelemetryQueueDirectory, protector,
            50L * 1024 * 1024, _json);
        var evidence = new EvidenceChain(paths.EvidenceLogPath, paths.EvidenceStatePath, protector, _json);
        Dictionary<string, FileFingerprint> previous = new(StringComparer.OrdinalIgnoreCase);
        string? previousScope = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = ActivityCollectorWorker.ReadPolicy(policyPath);
            if (policy.Enabled && policy.InvestigationAuthorized && policy.FileRoots.Count > 0)
            {
                try
                {
                    var scope = string.Join("|", policy.FileRoots.Order(StringComparer.OrdinalIgnoreCase));
                    var sameScope = string.Equals(scope, previousScope, StringComparison.Ordinal);
                    var current = Snapshot(policy.FileRoots, sameScope ? previous : null);
                    if (sameScope)
                    {
                        foreach (var change in Diff(previous, current))
                        {
                            var payload = new
                            {
                                timestampUtc = DateTimeOffset.UtcNow,
                                policy.Mode,
                                policy.CaseId,
                                change.Path,
                                change.Length,
                                change.Sha256
                            };
                            telemetry.Enqueue("File", change.Action, TelemetryPriority.High, payload);
                            evidence.Append(TenantId, identity.DeviceId, "File", change.Action, payload);
                        }
                    }
                    previous = current;
                    previousScope = scope;
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "File activity collection failed.");
                }
            }
            else
            {
                previous.Clear();
                previousScope = null;
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    internal static IReadOnlyList<FileChange> Diff(
        IReadOnlyDictionary<string, FileFingerprint> previous,
        IReadOnlyDictionary<string, FileFingerprint> current)
    {
        var result = new List<FileChange>();
        foreach (var (path, value) in current)
        {
            if (!previous.TryGetValue(path, out var old))
                result.Add(new("Create", path, value.Length, value.Sha256));
            else if (old.Length != value.Length || old.LastWriteUtc != value.LastWriteUtc ||
                     !string.Equals(old.Sha256, value.Sha256, StringComparison.Ordinal))
                result.Add(new("Change", path, value.Length, value.Sha256));
        }
        foreach (var (path, value) in previous)
            if (!current.ContainsKey(path))
                result.Add(new("Delete", path, value.Length, value.Sha256));
        return result.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, FileFingerprint> Snapshot(IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, FileFingerprint>? previous)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (result.Count >= MaximumFiles)
                        return result;
                    try
                    {
                        var info = new FileInfo(path);
                        var hash = previous is not null &&
                                   previous.TryGetValue(info.FullName, out var old) &&
                                   old.Length == info.Length && old.LastWriteUtc == info.LastWriteTimeUtc
                            ? old.Sha256
                            : info.Length <= MaximumHashBytes ? Hash(path) : null;
                        result[info.FullName] = new FileFingerprint(info.Length, info.LastWriteTimeUtc, hash);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return result;
    }

    private static string? Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return null; }
    }
}
