using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed record RiskAnalyticsPolicy(
    bool Enabled,
    string Mode,
    string? CaseId,
    DateTimeOffset ExpiresAtUtc,
    int WindowMinutes,
    int MassDownloadCount,
    long MassDownloadBytes);

internal sealed record RiskFinding(string Code, int Score, string Summary, long EventCount);
internal sealed record RiskReport(
    DateTimeOffset GeneratedAtUtc,
    string CaseId,
    string DeviceId,
    int Score,
    string Severity,
    double BaselineScore,
    IReadOnlyList<RiskFinding> Findings,
    long EvidenceEvents,
    string? LastEvidenceHash);
internal sealed record RiskBaseline(long Samples, double AverageScore, string? LastEvidenceHash);

internal sealed class RiskAnalyticsWorker : BackgroundService
{
    private const string TenantId = "investa";
    private readonly ILogger<RiskAnalyticsWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RiskAnalyticsWorker(ILogger<RiskAnalyticsWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = AgentPaths.CreateDefault();
        paths.EnsureDirectories();
        var protector = new DpapiMachineStateProtector();
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, protector).LoadOrCreate(TenantId);
        var telemetry = new TelemetryQueue(paths.TelemetryQueueDirectory, protector,
            50L * 1024 * 1024, _json);
        var evidence = new EvidenceChain(paths.EvidenceLogPath, paths.EvidenceStatePath, protector, _json);
        var policyPath = Path.Combine(paths.AgentDirectory, "active-policy.json");
        var baselinePath = Path.Combine(paths.AgentDirectory, "risk-baseline.bin");
        var reportPath = Path.Combine(paths.AgentDirectory, "risk-report.json");
        var htmlPath = Path.Combine(paths.AgentDirectory, "risk-report.html");
        var manifestPath = Path.Combine(paths.AgentDirectory, "risk-report-manifest.json");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var policy = ReadPolicy(policyPath);
                if (policy.Enabled && policy.Mode == "InsiderRisk" &&
                    !string.IsNullOrWhiteSpace(policy.CaseId) &&
                    policy.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    var events = ReadEvidence(paths.EvidenceLogPath, policy.CaseId!,
                        DateTimeOffset.UtcNow.AddMinutes(-policy.WindowMinutes), _json);
                    var lastHash = events.LastOrDefault()?.EventHash;
                    var baseline = LoadBaseline(baselinePath, protector, _json);
                    if (events.Count > 0 && !string.Equals(lastHash, baseline.LastEvidenceHash, StringComparison.Ordinal))
                    {
                        var report = Evaluate(policy, identity.DeviceId, events, baseline);
                        AtomicFile.WriteJson(reportPath, report, _json);
                        AtomicFile.WriteBytes(htmlPath, Encoding.UTF8.GetBytes(RenderHtml(report)));
                        var manifest = CreateManifest(reportPath, htmlPath, report);
                        AtomicFile.WriteJson(manifestPath, manifest, _json);
                        telemetry.Enqueue("Risk", "Assessment", Priority(report.Score), report);
                        evidence.Append(TenantId, identity.DeviceId, "Risk", "Assessment", report);
                        SaveBaseline(baselinePath, protector, new RiskBaseline(
                            baseline.Samples + 1,
                            ((baseline.AverageScore * baseline.Samples) + report.Score) / (baseline.Samples + 1),
                            lastHash), _json);
                    }
                }
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Risk analytics failed.");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal static RiskAnalyticsPolicy ReadPolicy(string path)
    {
        if (!File.Exists(path))
            return Disabled();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var mode = Text(root, "mode") ?? "Normal";
            var caseId = Text(root, "caseId");
            var expiry = root.TryGetProperty("expiresAtUtc", out var expires) &&
                         expires.TryGetDateTimeOffset(out var timestamp)
                ? timestamp : DateTimeOffset.MinValue;
            if (!root.TryGetProperty("settings", out var settings) ||
                !settings.TryGetProperty("riskAnalytics", out var risk) ||
                risk.ValueKind != JsonValueKind.Object)
                return Disabled() with { Mode = mode, CaseId = caseId, ExpiresAtUtc = expiry };
            var enabled = risk.TryGetProperty("enabled", out var enabledValue) &&
                          enabledValue.ValueKind == JsonValueKind.True;
            return new RiskAnalyticsPolicy(enabled, mode, caseId, expiry,
                Integer(risk, "windowMinutes", 60, 5, 10080),
                Integer(risk, "massDownloadCount", 20, 2, 10000),
                Long(risk, "massDownloadBytes", 500L * 1024 * 1024, 1, 1L << 50));
        }
        catch { return Disabled(); }
    }

    internal static RiskReport Evaluate(RiskAnalyticsPolicy policy, string deviceId,
        IReadOnlyList<EvidenceEvent> events, RiskBaseline baseline)
    {
        var browser = events.Where(value => value.Category == "Browser").ToArray();
        var downloads = browser.Where(value => BrowserType(value) == "download").ToArray();
        var uploads = browser.Where(value => BrowserType(value) is "uploadSelection" or "dragDrop" or "uploadResult").ToArray();
        var archives = FileNames(events).Count(IsArchive);
        var deletes = events.Count(value => value.Category == "File" && value.Action == "Delete");
        var usb = events.Count(value => value.Category == "Activity" && HasValue(value.Data, "usb"));
        var print = events.Count(value => value.Category == "Activity" && HasValue(value.Data, "printing"));
        var downloadBytes = downloads.Sum(BrowserBytes);
        var findings = new List<RiskFinding>();

        if (downloads.Length >= policy.MassDownloadCount || downloadBytes >= policy.MassDownloadBytes)
            findings.Add(new("MASS_DOWNLOAD", 30, "Mass download threshold exceeded.", downloads.Length));
        if (archives > 0)
            findings.Add(new("ARCHIVE_CREATED", 15, "Archive activity observed.", archives));
        if (uploads.Length > 0)
            findings.Add(new("UPLOAD_CHANNEL", 20, "Browser upload activity observed.", uploads.Length));
        if (downloads.Length > 0 && uploads.Length > 0)
            findings.Add(new("DOWNLOAD_TO_UPLOAD", 25, "Download followed by an upload channel.", downloads.Length + uploads.Length));
        if (archives > 0 && uploads.Length > 0)
            findings.Add(new("ARCHIVE_TO_UPLOAD", 20, "Archive activity correlated with upload.", archives + uploads.Length));
        if (deletes > 0 && uploads.Length > 0)
            findings.Add(new("UPLOAD_TO_DELETE", 20, "Deletion correlated with upload.", deletes + uploads.Length));
        if (usb > 0)
            findings.Add(new("USB_ACTIVITY", 10, "USB activity present in the case window.", usb));
        if (print > 0)
            findings.Add(new("PRINT_ACTIVITY", 10, "Printing activity present in the case window.", print));

        var raw = Math.Min(100, findings.Sum(value => value.Score));
        if (baseline.Samples >= 5 && raw >= baseline.AverageScore + 25)
            findings.Add(new("BASELINE_DEVIATION", 15, "Score is materially above the device baseline.", 1));
        var score = Math.Min(100, findings.Sum(value => value.Score));
        return new RiskReport(DateTimeOffset.UtcNow, policy.CaseId!, deviceId, score,
            score switch { >= 80 => "Critical", >= 55 => "High", >= 30 => "Medium", _ => "Low" },
            Math.Round(baseline.AverageScore, 2), findings, events.Count, events.LastOrDefault()?.EventHash);
    }

    private static List<EvidenceEvent> ReadEvidence(string path, string caseId, DateTimeOffset notBefore,
        JsonSerializerOptions json)
    {
        if (!File.Exists(path))
            return [];
        var result = new List<EvidenceEvent>();
        foreach (var line in File.ReadLines(path).TakeLast(10000))
        {
            try
            {
                var item = JsonSerializer.Deserialize<EvidenceEvent>(line, json);
                if (item is not null && item.Category != "Risk" && item.TimestampUtc >= notBefore &&
                    Contains(item.Data, JsonSerializer.Serialize(caseId)))
                    result.Add(item);
            }
            catch { }
        }
        return result;
    }

    private static object CreateManifest(string jsonPath, string htmlPath, RiskReport report) => new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        report.CaseId,
        report.DeviceId,
        report.LastEvidenceHash,
        files = new[]
        {
            new { path = Path.GetFileName(jsonPath), sha256 = Hash(jsonPath) },
            new { path = Path.GetFileName(htmlPath), sha256 = Hash(htmlPath) }
        }
    };

    private static string RenderHtml(RiskReport report)
    {
        var rows = string.Join("", report.Findings.Select(value =>
            $"<tr><td>{WebUtility.HtmlEncode(value.Code)}</td><td>{value.Score}</td><td>{WebUtility.HtmlEncode(value.Summary)}</td><td>{value.EventCount}</td></tr>"));
        return $$"""
                <!doctype html><html lang="en"><head><meta charset="utf-8">
                <title>SIRK Insider Risk Report</title>
                <style>body{font:14px Segoe UI,sans-serif;margin:32px;color:#172033}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccd3df;padding:8px;text-align:left}.score{font-size:32px;font-weight:700}</style>
                </head><body><h1>SIRK Insider Risk Report</h1>
                <p>Case: {{WebUtility.HtmlEncode(report.CaseId)}} | Device: {{WebUtility.HtmlEncode(report.DeviceId)}}</p>
                <p class="score">{{report.Score}}/100 — {{report.Severity}}</p>
                <p>Generated UTC: {{report.GeneratedAtUtc:O}} | Baseline: {{report.BaselineScore}}</p>
                <table><thead><tr><th>Finding</th><th>Score</th><th>Summary</th><th>Events</th></tr></thead><tbody>{{rows}}</tbody></table>
                </body></html>
                """;
    }

    private static string? BrowserType(EvidenceEvent item) =>
        item.Data.TryGetProperty("browserEvent", out var browser) ? Text(browser, "type") : null;
    private static long BrowserBytes(EvidenceEvent item) =>
        item.Data.TryGetProperty("browserEvent", out var browser) &&
        browser.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var value) ? value : 0;
    private static IEnumerable<string> FileNames(IEnumerable<EvidenceEvent> events) =>
        events.SelectMany(value => value.Data.TryGetProperty("browserEvent", out var browser) &&
                                          browser.TryGetProperty("files", out var files) &&
                                          files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Select(file => Text(file, "name")).OfType<string>()
            : []);
    private static bool IsArchive(string value) =>
        Path.GetExtension(value).ToLowerInvariant() is ".zip" or ".7z" or ".rar" or ".tar" or ".gz";
    private static bool Contains(JsonElement value, string text) =>
        value.GetRawText().Contains(text, StringComparison.OrdinalIgnoreCase);
    private static bool HasValue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
        !(property.ValueKind == JsonValueKind.Array && property.GetArrayLength() == 0);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static int Integer(JsonElement value, string name, int fallback, int min, int max) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number)
            ? Math.Clamp(number, min, max) : fallback;
    private static long Long(JsonElement value, string name, long fallback, long min, long max) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number)
            ? Math.Clamp(number, min, max) : fallback;
    private static TelemetryPriority Priority(int score) =>
        score >= 55 ? TelemetryPriority.Critical : score >= 30 ? TelemetryPriority.High : TelemetryPriority.Normal;
    private static RiskAnalyticsPolicy Disabled() =>
        new(false, "Normal", null, DateTimeOffset.MinValue, 60, 20, 500L * 1024 * 1024);

    private static RiskBaseline LoadBaseline(string path, IStateProtector protector, JsonSerializerOptions json)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RiskBaseline>(protector.Unprotect(File.ReadAllBytes(path)), json)
                  ?? new(0, 0, null)
                : new(0, 0, null);
        }
        catch { return new(0, 0, null); }
    }

    private static void SaveBaseline(string path, IStateProtector protector, RiskBaseline value,
        JsonSerializerOptions json) =>
        AtomicFile.WriteBytes(path, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value, json)));
}
