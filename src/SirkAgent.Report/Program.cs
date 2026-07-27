using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var agentDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
var reportDirectory = Path.Combine(agentDirectory, "Reports");
Directory.CreateDirectory(reportDirectory);

var timestamp = DateTimeOffset.Now;
var explicitHtmlPath = args.FirstOrDefault(a => a.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
var explicitJsonPath = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
var baseName = $"SIRK-Agent-Status-{timestamp:yyyyMMdd-HHmmss}";
var htmlPath = explicitHtmlPath ?? Path.Combine(reportDirectory, baseName + ".html");
var jsonPath = explicitJsonPath ?? Path.ChangeExtension(htmlPath, ".json");
var jsonOnly = args.Contains("--json-only", StringComparer.OrdinalIgnoreCase);
var noOpen = args.Contains("--no-open", StringComparer.OrdinalIgnoreCase) || jsonOnly;

var modules = new List<ModuleResult>
{
    InspectJson("Policy / Heartbeat", Path.Combine(agentDirectory, "heartbeat-latest.json"), true),
    InspectJson("Quarantine", Path.Combine(agentDirectory, "quarantine-status.json"), false),
    InspectJson("Latest tamper event", Path.Combine(agentDirectory, "tamper-event-latest.json"), false),
    InspectText("Agent events", Path.Combine(agentDirectory, "agent-events.jsonl"), false, 100),
    InspectBinary("Policy state", Path.Combine(agentDirectory, "policy-state.bin"), true),
    InspectBinary("Protected quarantine state", Path.Combine(agentDirectory, "quarantine-state.bin"), false),
    InspectRuntime(),
    InspectEnvironment(agentDirectory)
};

var overall = modules.Any(m => m.Status == ModuleStatus.Critical) ? ModuleStatus.Critical
    : modules.Any(m => m.Status == ModuleStatus.Warning) ? ModuleStatus.Warning : ModuleStatus.Healthy;

var export = new DiagnosticExport(
    SchemaVersion: 1,
    GeneratedUtc: timestamp.ToUniversalTime(),
    GeneratedLocal: timestamp,
    DeviceName: Environment.MachineName,
    AgentDirectory: agentDirectory,
    OverallStatus: overall,
    Summary: new DiagnosticSummary(
        Healthy: modules.Count(m => m.Status == ModuleStatus.Healthy),
        Warning: modules.Count(m => m.Status == ModuleStatus.Warning),
        Critical: modules.Count(m => m.Status == ModuleStatus.Critical),
        Total: modules.Count),
    Modules: modules);

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
File.WriteAllText(jsonPath, JsonSerializer.Serialize(export, jsonOptions), new UTF8Encoding(false));
Console.WriteLine($"JSON report: {jsonPath}");

if (!jsonOnly)
{
    File.WriteAllText(htmlPath, BuildHtml(overall, modules, agentDirectory, jsonPath), new UTF8Encoding(false));
    Console.WriteLine($"HTML report: {htmlPath}");

    if (!noOpen)
    {
        try { Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine($"Nie mozna otworzyc raportu: {ex.Message}"); }
    }
}

return overall == ModuleStatus.Critical ? 2 : 0;

static ModuleResult InspectJson(string name, string path, bool required)
{
    if (!File.Exists(path)) return Missing(name, path, required);
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var status = ModuleStatus.Healthy;
        var summary = "Plik JSON poprawny.";
        if ((root.TryGetProperty("tamperDetected", out var tamper) && tamper.ValueKind == JsonValueKind.True) ||
            (root.TryGetProperty("quarantineActive", out var quarantine) && quarantine.ValueKind == JsonValueKind.True) ||
            (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True))
        { status = ModuleStatus.Critical; summary = "Wykryto manipulacje lub aktywna kwarantanne."; }
        else if (root.TryGetProperty("stateStatus", out var state) && !string.Equals(state.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        { status = ModuleStatus.Warning; summary = "Stan modulu nie jest OK."; }
        return new(name, status, summary, path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }), null);
    }
    catch (Exception ex) { return new(name, ModuleStatus.Critical, "Nie mozna odczytac JSON.", path, null, ex.ToString()); }
}

static ModuleResult InspectText(string name, string path, bool required, int tailLines)
{
    if (!File.Exists(path)) return Missing(name, path, required);
    try
    {
        var lines = File.ReadLines(path).TakeLast(tailLines).ToArray();
        return new(name, lines.Length == 0 ? ModuleStatus.Warning : ModuleStatus.Healthy,
            $"Odczytano ostatnie {lines.Length} wpisow.", path,
            lines.Length == 0 ? "Plik jest pusty." : string.Join(Environment.NewLine, lines), null);
    }
    catch (Exception ex) { return new(name, ModuleStatus.Critical, "Nie mozna odczytac logu.", path, null, ex.ToString()); }
}

static ModuleResult InspectBinary(string name, string path, bool required)
{
    if (!File.Exists(path)) return Missing(name, path, required);
    try
    {
        var info = new FileInfo(path);
        return new(name, info.Length > 0 ? ModuleStatus.Healthy : ModuleStatus.Critical,
            info.Length > 0 ? "Plik istnieje i nie jest pusty." : "Plik jest pusty.", path,
            $"Rozmiar: {info.Length} bajtow\nOstatnia zmiana UTC: {info.LastWriteTimeUtc:O}", null);
    }
    catch (Exception ex) { return new(name, ModuleStatus.Critical, "Nie mozna sprawdzic pliku.", path, null, ex.ToString()); }
}

static ModuleResult InspectRuntime()
{
    try
    {
        var version = Environment.Version;
        var details = $"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
                      $"OS architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\n" +
                      $"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        return new(".NET Runtime", version.Major >= 8 ? ModuleStatus.Healthy : ModuleStatus.Warning, $"Runtime {version}", null, details, null);
    }
    catch (Exception ex) { return new(".NET Runtime", ModuleStatus.Critical, "Brak danych runtime.", null, null, ex.ToString()); }
}

static ModuleResult InspectEnvironment(string agentDirectory)
{
    try
    {
        var root = Path.GetPathRoot(agentDirectory) ?? throw new InvalidOperationException("Brak katalogu glownego dysku.");
        var drive = new DriveInfo(root);
        var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        var status = freeGb < 1 ? ModuleStatus.Critical : freeGb < 5 ? ModuleStatus.Warning : ModuleStatus.Healthy;
        var details = $"Computer: {Environment.MachineName}\nUser: {Environment.UserDomainName}\\{Environment.UserName}\nOS: {Environment.OSVersion}\n" +
                      $"64-bit OS: {Environment.Is64BitOperatingSystem}\nCPU: {Environment.ProcessorCount}\nFree disk: {freeGb:F2} GB\nAgent: {agentDirectory}";
        return new("System / Environment", status, $"Wolne miejsce: {freeGb:F2} GB", agentDirectory, details, null);
    }
    catch (Exception ex) { return new("System / Environment", ModuleStatus.Critical, "Nie mozna odczytac danych systemowych.", agentDirectory, null, ex.ToString()); }
}

static ModuleResult Missing(string name, string path, bool required) => new(name,
    required ? ModuleStatus.Critical : ModuleStatus.Warning,
    required ? "Brak wymaganego pliku." : "Plik jeszcze nie zostal utworzony.", path, null, null);

static string BuildHtml(ModuleStatus overall, IReadOnlyList<ModuleResult> modules, string agentDirectory, string jsonPath)
{
    static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    static string Css(ModuleStatus value) => value.ToString().ToLowerInvariant();
    static string Label(ModuleStatus value) => value == ModuleStatus.Healthy ? "OK" : value == ModuleStatus.Warning ? "OSTRZEZENIE" : "KRYTYCZNY";
    var body = new StringBuilder();
    foreach (var module in modules)
    {
        body.Append("<details class='module ").Append(Css(module.Status)).Append("'><summary><span class='dot'></span><strong>")
            .Append(E(module.Name)).Append("</strong><span class='badge'>").Append(Label(module.Status)).Append("</span><span class='summary'>")
            .Append(E(module.Summary)).Append("</span></summary><div class='body'>");
        if (!string.IsNullOrWhiteSpace(module.Path)) body.Append("<p><b>Sciezka:</b> <code>").Append(E(module.Path)).Append("</code></p>");
        if (!string.IsNullOrWhiteSpace(module.Details)) body.Append("<h3>Szczegoly</h3><pre>").Append(E(module.Details)).Append("</pre>");
        if (!string.IsNullOrWhiteSpace(module.Error)) body.Append("<h3>Blad</h3><pre class='error'>").Append(E(module.Error)).Append("</pre>");
        body.Append("</div></details>");
    }
    var css = ":root{color-scheme:dark;--bg:#0b1220;--card:#111a2b;--text:#e8eef8;--muted:#9fb0c8;--ok:#24c875;--warn:#f0b429;--bad:#f05252;--line:#26344d}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 Segoe UI,Arial,sans-serif}main{max-width:1200px;margin:auto;padding:28px}header{display:flex;justify-content:space-between;gap:20px;align-items:center;margin-bottom:22px}h1{margin:0}.meta,.summary,footer{color:var(--muted)}.overall{padding:10px 16px;border-radius:999px;font-weight:700}.overall.healthy{background:#123c2a}.overall.warning{background:#443615}.overall.critical{background:#4b1f25}.module{background:var(--card);border:1px solid var(--line);border-left:5px solid var(--line);border-radius:12px;margin:10px 0;overflow:hidden}.module.healthy{border-left-color:var(--ok)}.module.warning{border-left-color:var(--warn)}.module.critical{border-left-color:var(--bad)}summary{display:grid;grid-template-columns:14px minmax(180px,1fr) auto minmax(240px,2fr);gap:12px;align-items:center;padding:15px 18px;cursor:pointer}.dot{width:10px;height:10px;border-radius:50%;background:var(--line)}.healthy .dot{background:var(--ok)}.warning .dot{background:var(--warn)}.critical .dot{background:var(--bad)}.badge{font-size:11px;font-weight:800;padding:3px 8px;border:1px solid var(--line);border-radius:999px}.body{border-top:1px solid var(--line);padding:16px 18px}pre{white-space:pre-wrap;word-break:break-word;background:#07101e;padding:14px;border-radius:8px;max-height:520px;overflow:auto}pre.error{border:1px solid var(--bad)}code{color:#bdd8ff}footer{margin-top:22px}";
    return "<!doctype html><html lang='pl'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>SIRK Agent - raport</title><style>" + css +
           "</style></head><body><main><header><div><h1>SIRK Agent - raport stanu</h1><div class='meta'>Urzadzenie: " + E(Environment.MachineName) + " | Wygenerowano: " + E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")) +
           "</div></div><div class='overall " + Css(overall) + "'>STAN: " + Label(overall) + "</div></header><section>" + body +
           "</section><footer>Katalog agenta: " + E(agentDirectory) + ". JSON do wyslania: <code>" + E(jsonPath) + "</code>. Kliknij modul, aby rozwinac szczegoly i bledy.</footer></main></body></html>";
}

enum ModuleStatus { Healthy, Warning, Critical }
sealed record ModuleResult(string Name, ModuleStatus Status, string Summary, string? Path, string? Details, string? Error);
sealed record DiagnosticSummary(int Healthy, int Warning, int Critical, int Total);
sealed record DiagnosticExport(int SchemaVersion, DateTimeOffset GeneratedUtc, DateTimeOffset GeneratedLocal, string DeviceName, string AgentDirectory, ModuleStatus OverallStatus, DiagnosticSummary Summary, IReadOnlyList<ModuleResult> Modules);