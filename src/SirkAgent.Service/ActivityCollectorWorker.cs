using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed record ActivityCollectionPolicy(
    bool Enabled,
    bool CollectProcesses,
    bool CollectInteractiveContext,
    bool CollectClipboardMetadata,
    bool CollectUsb,
    bool CollectPrinting,
    IReadOnlyList<string> FileRoots,
    int IntervalSeconds,
    string Mode,
    string? CaseId,
    DateTimeOffset ExpiresAtUtc)
{
    public bool InvestigationAuthorized =>
        Mode is "Investigation" or "InsiderRisk" &&
        !string.IsNullOrWhiteSpace(CaseId) &&
        ExpiresAtUtc > DateTimeOffset.UtcNow;
}

internal sealed class ActivityCollectorWorker : BackgroundService
{
    private const string TenantId = "investa";
    private readonly ILogger<ActivityCollectorWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ActivityCollectorWorker(ILogger<ActivityCollectorWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = AgentPaths.CreateDefault();
        paths.EnsureDirectories();
        var policyPath = Path.Combine(paths.AgentDirectory, "active-policy.json");
        var outputPath = Path.Combine(paths.AgentDirectory, "activity-latest.json");
        var protector = new DpapiMachineStateProtector();
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, protector).LoadOrCreate(TenantId);
        var telemetry = new TelemetryQueue(paths.TelemetryQueueDirectory, protector,
            50L * 1024 * 1024, _json);
        var evidence = new EvidenceChain(paths.EvidenceLogPath, paths.EvidenceStatePath, protector, _json);

        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = ReadPolicy(policyPath);
            if (policy.Enabled)
            {
                try
                {
                    var snapshot = await CollectAsync(policy, stoppingToken);
                    AtomicFile.WriteJson(outputPath, snapshot, _json);
                    telemetry.Enqueue("Activity", "Snapshot", TelemetryPriority.Normal, snapshot);
                    evidence.Append(TenantId, identity.DeviceId, "Activity", "Snapshot", snapshot);
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "Activity collection failed.");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(policy.IntervalSeconds, 30, 3600)),
                stoppingToken);
        }
    }

    internal async Task<object> CollectAsync(ActivityCollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        var processes = policy.CollectProcesses ? ProcessSnapshot() : null;
        JsonElement? interactive = policy.CollectInteractiveContext && policy.InvestigationAuthorized
            ? await InteractiveSnapshotAsync(cancellationToken) : null;
        var usb = policy.CollectUsb ? await PowerShellJsonAsync(
            "Get-PnpDevice -Class USB -PresentOnly | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Json -Compress",
            cancellationToken) : null;
        var printing = policy.CollectPrinting && policy.InvestigationAuthorized ? await PowerShellJsonAsync(
            "Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-PrintService/Operational';StartTime=(Get-Date).AddMinutes(-15)} -ErrorAction SilentlyContinue | Select-Object -First 50 TimeCreated,Id,RecordId,Message | ConvertTo-Json -Compress",
            cancellationToken) : null;
        var files = policy.InvestigationAuthorized ? FileSnapshot(policy.FileRoots) : [];

        if (!policy.InvestigationAuthorized && (policy.CollectInteractiveContext ||
                                                 policy.CollectClipboardMetadata ||
                                                 policy.CollectPrinting ||
                                                 policy.FileRoots.Count > 0))
            _logger.LogWarning("Detailed activity settings ignored because Investigation/InsiderRisk authorization is absent or expired.");

        return new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            policy.Mode,
            policy.CaseId,
            policy.ExpiresAtUtc,
            investigationAuthorized = policy.InvestigationAuthorized,
            processes,
            interactive = RedactClipboard(interactive, policy.CollectClipboardMetadata),
            usb,
            printing,
            files
        };
    }

    internal static ActivityCollectionPolicy ReadPolicy(string path)
    {
        if (!File.Exists(path))
            return Disabled();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var mode = root.TryGetProperty("mode", out var modeValue) ? modeValue.GetString() ?? "Normal" : "Normal";
            var caseId = root.TryGetProperty("caseId", out var caseValue) && caseValue.ValueKind == JsonValueKind.String
                ? caseValue.GetString() : null;
            var expires = root.TryGetProperty("expiresAtUtc", out var expiry) &&
                          expiry.TryGetDateTimeOffset(out var expiryValue)
                ? expiryValue : DateTimeOffset.MinValue;
            if (!root.TryGetProperty("settings", out var settings) ||
                !settings.TryGetProperty("activityCollection", out var activity) ||
                activity.ValueKind != JsonValueKind.Object)
                return Disabled() with { Mode = mode, CaseId = caseId, ExpiresAtUtc = expires };
            bool Flag(string name) => activity.TryGetProperty(name, out var value) &&
                                      value.ValueKind == JsonValueKind.True;
            var roots = activity.TryGetProperty("fileRoots", out var fileRoots) &&
                        fileRoots.ValueKind == JsonValueKind.Array
                ? fileRoots.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => Path.GetFullPath(value!)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20).ToArray()
                : [];
            var interval = activity.TryGetProperty("intervalSeconds", out var intervalValue) &&
                           intervalValue.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 30, 3600) : 300;
            return new ActivityCollectionPolicy(Flag("enabled"), Flag("processes"),
                Flag("interactiveContext"), Flag("clipboardMetadata"), Flag("usb"),
                Flag("printing"), roots, interval, mode, caseId, expires);
        }
        catch
        {
            return Disabled();
        }
    }

    private static ActivityCollectionPolicy Disabled() =>
        new(false, false, false, false, false, false, [], 300, "Normal", null,
            DateTimeOffset.MinValue);

    private static object[] ProcessSnapshot() =>
        Process.GetProcesses().Select(process =>
        {
            try
            {
                return (object)new
                {
                    process.Id,
                    process.ProcessName,
                    sessionId = process.SessionId,
                    startTimeUtc = Try(() => process.StartTime.ToUniversalTime()),
                    path = Try(() => process.MainModule?.FileName)
                };
            }
            finally { process.Dispose(); }
        }).Take(4096).ToArray();

    private static object[] FileSnapshot(IReadOnlyList<string> roots)
    {
        var result = new List<object>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(2000))
                {
                    var file = new FileInfo(path);
                    result.Add(new { path = file.FullName, file.Length, lastWriteUtc = file.LastWriteTimeUtc });
                }
            }
            catch (Exception error)
            {
                result.Add(new { path = root, error = error.GetType().Name });
            }
        }
        return result.ToArray();
    }

    private async Task<JsonElement?> InteractiveSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", "SIRK-Agent-Interactive-Session",
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync("{\"type\":\"activity\"}");
        var line = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(line))
            return null;
        using var response = JsonDocument.Parse(line);
        return response.RootElement.TryGetProperty("data", out var data) ? data.Clone() : null;
    }

    private static JsonElement? RedactClipboard(JsonElement? interactive, bool clipboardAllowed)
    {
        if (interactive is null || clipboardAllowed)
            return interactive;
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(interactive.Value.GetRawText())
                         ?? [];
        dictionary.Remove("clipboard");
        return JsonSerializer.SerializeToElement(dictionary);
    }

    private async Task<JsonElement?> PowerShellJsonAsync(string command, CancellationToken cancellationToken)
    {
        var probe = await new ProcessCommandProbe().RunAsync("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "`\"") + "\"",
            TimeSpan.FromSeconds(45), cancellationToken);
        if (probe.ExitCode != 0 || string.IsNullOrWhiteSpace(probe.Output))
            return null;
        try
        {
            using var document = JsonDocument.Parse(probe.Output);
            return document.RootElement.Clone();
        }
        catch { return null; }
    }

    private static T? Try<T>(Func<T?> callback)
    {
        try { return callback(); } catch { return default; }
    }
}
