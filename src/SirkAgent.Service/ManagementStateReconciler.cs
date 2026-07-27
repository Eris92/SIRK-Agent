using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed class ManagementStateReconciler : BackgroundService
{
    private readonly ILogger<ManagementStateReconciler> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ManagementStateReconciler(ILogger<ManagementStateReconciler> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        Directory.CreateDirectory(root);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Reconcile(root);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Management state reconciliation failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void Reconcile(string root)
    {
        var statePath = Path.Combine(root, "management-state.json");
        if (!File.Exists(statePath))
            return;

        var activePolicyPath = Path.Combine(root, "active-policy.json");
        var acceptedDirectory = Path.Combine(root, "Archive", "Accepted");
        var rejectedDirectory = Path.Combine(root, "Archive", "Rejected");

        var current = JsonSerializer.Deserialize<PersistedManagementState>(File.ReadAllBytes(statePath), _json);
        if (current is null)
            return;

        var accepted = CountResultFiles(acceptedDirectory, "*.accepted.json");
        var rejected = CountResultFiles(rejectedDirectory, "*.rejected.json");
        var activePolicyId = ReadPolicyId(activePolicyPath);

        var reconciledAccepted = Math.Max(current.AcceptedPolicies, accepted);
        var reconciledRejected = Math.Max(current.RejectedPolicies, rejected);
        var reconciledPolicyId = string.IsNullOrWhiteSpace(current.LastPolicyId) ? activePolicyId : current.LastPolicyId;

        if (reconciledAccepted == current.AcceptedPolicies &&
            reconciledRejected == current.RejectedPolicies &&
            string.Equals(reconciledPolicyId, current.LastPolicyId, StringComparison.Ordinal))
            return;

        var updated = current with
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            LastPolicyId = reconciledPolicyId,
            AcceptedPolicies = reconciledAccepted,
            RejectedPolicies = reconciledRejected
        };

        AtomicFile.WriteJson(statePath, updated, _json);
    }

    private static long CountResultFiles(string directory, string filter) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, filter, SearchOption.TopDirectoryOnly).LongCount()
            : 0;

    private string? ReadPolicyId(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("policyId", out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record PersistedManagementState(
        DateTimeOffset TimestampUtc,
        string Status,
        string Code,
        string? LastPolicyId,
        string? LastError,
        long AcceptedPolicies,
        long RejectedPolicies,
        bool IntegrityVerified,
        string? IntegrityCode);
}
