using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SirkAgent.Service;

internal sealed class EnduranceWorker : BackgroundService
{
    private const int MaxSamples = 576;
    private readonly ILogger<EnduranceWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public EnduranceWorker(ILogger<EnduranceWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        Directory.CreateDirectory(root);
        var samplesPath = Path.Combine(root, "endurance-samples.jsonl");
        var summaryPath = Path.Combine(root, "endurance-summary.json");
        var htmlPath = Path.Combine(root, "endurance-report.html");
        var interval = ResolveInterval();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var samples = LoadSamples(samplesPath);
                var sample = CaptureSample(root, samples);
                if (sample is not null)
                {
                    samples.Add(sample);
                    if (samples.Count > MaxSamples)
                        samples = samples.TakeLast(MaxSamples).ToList();

                    WriteSamples(samplesPath, samples);
                    var summary = BuildSummary(samples, interval);
                    WriteJsonAtomic(summaryPath, summary);
                    WriteHtmlAtomic(htmlPath, summary, samples);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to update endurance report.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static TimeSpan ResolveInterval()
    {
        var raw = Environment.GetEnvironmentVariable("SIRK_ENDURANCE_INTERVAL_SECONDS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300))
            : TimeSpan.FromMinutes(5);
    }

    private static EnduranceSample? CaptureSample(string root, IReadOnlyCollection<EnduranceSample> existingSamples)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        using var runtime = ReadDocument(Path.Combine(root, "runtime-health.json"));
        using var heartbeat = ReadDocument(Path.Combine(root, "heartbeat-latest.json"));
        using var security = ReadDocument(Path.Combine(root, "security-state.json"));

        var deviceId = ReadString(heartbeat, "deviceId");
        var overallHealth = ReadString(security, "overallHealth");
        var securityState = ReadNestedString(security, "security", "state");
        var heartbeatFresh = ReadBool(runtime, "heartbeatFresh");
        if (runtime is null || heartbeat is null || security is null || string.IsNullOrWhiteSpace(deviceId) ||
            string.IsNullOrWhiteSpace(overallHealth) || string.IsNullOrWhiteSpace(securityState))
            return null;

        var sampleHealthy = heartbeatFresh &&
            string.Equals(overallHealth, "Healthy", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(securityState, "Operational", StringComparison.OrdinalIgnoreCase);
        var currentPidHasBaseline = existingSamples.Any(x => x.ProcessId == Environment.ProcessId);
        if (!currentPidHasBaseline && !sampleHealthy)
            return null;

        var queue = DirectorySize(Path.Combine(root, "TelemetryQueue"), "*.bin");
        var evidencePath = Path.Combine(root, "evidence-events.jsonl");
        var eventPath = Path.Combine(root, "agent-events.jsonl");

        return new EnduranceSample(
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(false),
            ReadDouble(runtime, "cpuPercent"),
            heartbeatFresh,
            overallHealth,
            securityState,
            deviceId,
            queue.Files,
            queue.Bytes,
            File.Exists(evidencePath) ? new FileInfo(evidencePath).Length : 0,
            File.Exists(eventPath) ? new FileInfo(eventPath).Length : 0);
    }

    internal static EnduranceSummary BuildSummary(IReadOnlyList<EnduranceSample> samples, TimeSpan interval)
    {
        var first = samples.First();
        var last = samples.Last();
        var restarts = samples.Zip(samples.Skip(1), (a, b) => a.ProcessId != b.ProcessId ? 1 : 0).Sum();
        var trendSamples = samples.Reverse().TakeWhile(x => x.ProcessId == last.ProcessId).Reverse().ToArray();
        var healthWindow = trendSamples.TakeLast(48).ToArray();
        var allowedGap = TimeSpan.FromTicks(interval.Ticks * 3);
        var gaps = healthWindow.Zip(healthWindow.Skip(1), (a, b) =>
            a.ProcessId == b.ProcessId && b.TimestampUtc - a.TimestampUtc > allowedGap ? 1 : 0).Sum();
        var unhealthy = healthWindow.Count(x => !x.HeartbeatFresh ||
            !string.Equals(x.OverallHealth, "Healthy", StringComparison.OrdinalIgnoreCase));
        var stableTrend = trendSamples.Length >= 12 ? trendSamples.Skip(2).ToArray() : trendSamples;
        var trendFirst = stableTrend.First();
        var memoryGrowth = stableTrend.Length >= 2 ? last.WorkingSetBytes - trendFirst.WorkingSetBytes : 0;
        var durationHours = stableTrend.Length >= 2
            ? Math.Max(0.001, (last.TimestampUtc - trendFirst.TimestampUtc).TotalHours) : 0;
        var growthPerHour = durationHours > 0 ? memoryGrowth / durationHours : 0;
        var leakSuspected = trendSamples.Length >= 12 && durationHours >= 0.5 &&
                            growthPerHour > 5L * 1024 * 1024;

        return new EnduranceSummary(
            DateTimeOffset.UtcNow,
            samples.Count,
            first.TimestampUtc,
            last.TimestampUtc,
            Math.Max(0, (last.TimestampUtc - first.TimestampUtc).TotalHours),
            restarts,
            gaps,
            unhealthy,
            samples.Min(x => x.CpuPercent),
            samples.Average(x => x.CpuPercent),
            samples.Max(x => x.CpuPercent),
            samples.Min(x => x.WorkingSetBytes),
            (long)samples.Average(x => x.WorkingSetBytes),
            samples.Max(x => x.WorkingSetBytes),
            memoryGrowth,
            growthPerHour,
            leakSuspected,
            last.DeviceId,
            last.TelemetryFiles,
            last.TelemetryBytes,
            last.EvidenceBytes,
            last.EventLogBytes,
            leakSuspected || gaps > 0 || unhealthy > 0 ? "Warning" : "Healthy");
    }

    private static List<EnduranceSample> LoadSamples(string path)
    {
        var result = new List<EnduranceSample>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var sample = JsonSerializer.Deserialize<EnduranceSample>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (sample is not null) result.Add(sample);
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    private static void WriteSamples(string path, IReadOnlyList<EnduranceSample> samples)
    {
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            foreach (var sample in samples)
                writer.WriteLine(JsonSerializer.Serialize(sample, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        File.Move(temp, path, true);
    }

    private void WriteJsonAtomic(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, _json), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static void WriteHtmlAtomic(string path, EnduranceSummary summary, IReadOnlyList<EnduranceSample> samples)
    {
        static string Mb(double value) => (value / 1024d / 1024d).ToString("0.00", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"pl\"><head><meta charset=\"utf-8\"><title>SIRK Agent Endurance</title>");
        builder.Append("<style>body{font-family:Segoe UI,Arial;margin:24px;background:#111827;color:#e5e7eb}.card{background:#1f2937;padding:16px;border-radius:10px;margin:10px 0}table{width:100%;border-collapse:collapse}td,th{padding:7px;border-bottom:1px solid #374151;text-align:left}.Healthy{color:#34d399}.Warning{color:#fbbf24}</style></head><body>");
        builder.Append("<h1>SIRK Agent — Endurance</h1><div class=\"card\">");
        builder.Append($"<h2 class=\"{summary.Status}\">{summary.Status}</h2>");
        builder.Append($"<p>Próbki: {summary.SampleCount} | Okres: {summary.DurationHours:0.00} h | Restarty procesu: {summary.ProcessRestarts} | Przerwy: {summary.SampleGaps}</p>");
        builder.Append($"<p>CPU min/avg/max: {summary.CpuMin:0.00}% / {summary.CpuAverage:0.00}% / {summary.CpuMax:0.00}%</p>");
        builder.Append($"<p>RAM min/avg/max: {Mb(summary.WorkingSetMin)} / {Mb(summary.WorkingSetAverage)} / {Mb(summary.WorkingSetMax)} MB</p>");
        builder.Append($"<p>Trend RAM: {Mb(summary.WorkingSetGrowthPerHour)}/h | Podejrzenie wycieku: {summary.MemoryLeakSuspected}</p>");
        builder.Append($"<p>Telemetry: {summary.TelemetryFiles} plików, {Mb(summary.TelemetryBytes)} MB | Evidence: {Mb(summary.EvidenceBytes)} MB</p></div>");
        builder.Append("<div class=\"card\"><table><thead><tr><th>UTC</th><th>PID</th><th>CPU %</th><th>RAM MB</th><th>Heartbeat</th><th>Health</th></tr></thead><tbody>");
        foreach (var sample in samples.TakeLast(48).Reverse())
        {
            builder.Append($"<tr><td>{sample.TimestampUtc:O}</td><td>{sample.ProcessId}</td><td>{sample.CpuPercent:0.00}</td><td>{Mb(sample.WorkingSetBytes)}</td><td>{sample.HeartbeatFresh}</td><td>{System.Net.WebUtility.HtmlEncode(sample.OverallHealth)}</td></tr>");
        }
        builder.Append("</tbody></table></div></body></html>");
        var temp = path + ".tmp";
        File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static JsonDocument? ReadDocument(string path)
    {
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllBytes(path)) : null; }
        catch { return null; }
    }

    private static string? ReadString(JsonDocument? document, string property) =>
        document is not null && document.RootElement.TryGetProperty(property, out var value) ? value.ToString() : null;
    private static string? ReadNestedString(JsonDocument? document, string parent, string property) =>
        document is not null && document.RootElement.TryGetProperty(parent, out var node) && node.TryGetProperty(property, out var value) ? value.ToString() : null;
    private static bool ReadBool(JsonDocument? document, string property) =>
        document is not null && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static double ReadDouble(JsonDocument? document, string property) =>
        document is not null && document.RootElement.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : 0;
    private static (int Files, long Bytes) DirectorySize(string path, string pattern)
    {
        if (!Directory.Exists(path)) return (0, 0);
        var files = Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).Select(x => new FileInfo(x)).ToArray();
        return (files.Length, files.Sum(x => x.Length));
    }
}

internal sealed record EnduranceSample(DateTimeOffset TimestampUtc, int ProcessId, long WorkingSetBytes,
    long PrivateMemoryBytes, long ManagedMemoryBytes, double CpuPercent, bool HeartbeatFresh,
    string? OverallHealth, string? SecurityState, string? DeviceId, int TelemetryFiles,
    long TelemetryBytes, long EvidenceBytes, long EventLogBytes);

internal sealed record EnduranceSummary(DateTimeOffset GeneratedAtUtc, int SampleCount,
    DateTimeOffset FirstSampleUtc, DateTimeOffset LastSampleUtc, double DurationHours,
    int ProcessRestarts, int SampleGaps, int UnhealthySamples, double CpuMin, double CpuAverage,
    double CpuMax, long WorkingSetMin, long WorkingSetAverage, long WorkingSetMax,
    long WorkingSetGrowthBytes, double WorkingSetGrowthPerHour, bool MemoryLeakSuspected,
    string? DeviceId, int TelemetryFiles, long TelemetryBytes, long EvidenceBytes,
    long EventLogBytes, string Status);
