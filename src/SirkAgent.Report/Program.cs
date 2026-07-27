using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
var agentDirectory = Path.Combine(programData, "SIRK", "Agent");
var reportDirectory = Path.Combine(agentDirectory, "Reports");
Directory.CreateDirectory(reportDirectory);

var outputPath = args.FirstOrDefault(a => a.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                 ?? Path.Combine(reportDirectory, $"SIRK-Agent-Status-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.html");

var modules = new List<ModuleResult>
{
    InspectJson("Policy / Heartbeat", Path.Combine(agentDirectory, "heartbeat-latest.json"), required: true),
    InspectJson("Quarantine", Path.Combine(agentDirectory, "quarantine-status.json"), required: false),
    InspectJson("Latest tamper event", Path.Combine(agentDirectory, "tamper-event-latest.json"), required: false),
    InspectText("Agent events", Path.Combine(agentDirectory, "agent-events.jsonl"), required: false, tailLines: 100),
    InspectBinary("Policy state", Path.Combine(agentDirectory, "policy-state.bin"), required: true),
    InspectBinary("Protected quarantine state", Path.Combine(agentDirectory, "quarantine-state.bin"), required: false),
    InspectRuntime(),
    InspectEnvironment(agentDirectory)
};

var overall = modules.Any(m => m.Status == ModuleStatus.Critical)
    ? ModuleStatus.Critical
    : modules.Any(m => m.Status == ModuleStatus.Warning)
        ? ModuleStatus.Warning
        : ModuleStatus.Healthy;

var html = BuildHtml(overall, modules, agentDirectory);
File.WriteAllText(outputPath, html, new UTF8Encoding(false));
Console.WriteLine($"Report: {outputPath}");

if (!args.Contains("--no-open", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Nie mozna otworzyc raportu automatycznie: {ex.Message}");
    }
}

return overall == ModuleStatus.Critical ? 2 : 0;

static ModuleResult InspectJson(string name, string path, bool required)
{
    if (!File.Exists(path))
        return Missing(name, path, required);

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        var status = ModuleStatus.Healthy;
        var summary = "Plik JSON poprawny.";

        if (document.RootElement.TryGetProperty("tamperDetected", out var tamper) && tamper.ValueKind == JsonValueKind.True)
        {
            status = ModuleStatus.Critical;
            summary = "Wykryto manipulacje.";
        }
        else if (document.RootElement.TryGetProperty("quarantineActive", out var quarantine) && quarantine.ValueKind == JsonValueKind.True)
        {
            status = ModuleStatus.Critical;
            summary = "Kwarantanna aktywna.";
        }
        else if (document.RootElement.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True)
        {
            status = ModuleStatus.Critical;
            summary = "Kwarantanna aktywna.";
        }
        else if (document.RootElement.TryGetProperty("stateStatus", out var stateStatus) &&
                 !string.Equals(stateStatus.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            status = ModuleStatus.Warning;
            summary = "Stan modulu nie jest OK.";
        }

        return new(name, status, summary, path, formatted, null);
    }
    catch (Exception ex)
    {
        return new(name, ModuleStatus.Critical, "Nie mozna odczytac lub sparsowac pliku JSON.", path, null, ex.ToString());
    }
}

static ModuleResult InspectText(string name, string path, bool required, int tailLines)
{
    if (!File.Exists(path))
        return Missing(name, path, required);

    try
    {
        var lines = File.ReadLines(path).TakeLast(tailLines).ToArray();
        var details = lines.Length == 0 ? "Plik jest pusty." : string.Join(Environment.NewLine, lines);
        return new(name, lines.Length == 0 ? ModuleStatus.Warning : ModuleStatus.Healthy,
            $"Odczytano ostatnie {lines.Length} wpisow.", path, details, null);
    }
    catch (Exception ex)
    {
        return new(name, ModuleStatus.Critical, "Nie mozna odczytac logu.", path, null, ex.ToString());
    }
}

static ModuleResult InspectBinary(string name, string path, bool required)
{
    if (!File.Exists(path))
        return Missing(name, path, required);

    try
    {
        var info = new FileInfo(path);
        var status = info.Length > 0 ? ModuleStatus.Healthy : ModuleStatus.Critical;
        return new(name, status, info.Length > 0 ? "Plik istnieje i nie jest pusty." : "Plik jest pusty.", path,
            $"Rozmiar: {info.Length} bajtow\nOstatnia zmiana UTC: {info.LastWriteTimeUtc:O}", null);
    }
    catch (Exception ex)
    {
        return new(name, ModuleStatus.Critical, "Nie mozna sprawdzic pliku.", path, null, ex.ToString());
    }
}

static ModuleResult InspectRuntime()
{
    try
    {
        var version = Environment.Version;
        return new(".NET Runtime", version.Major >= 8 ? ModuleStatus.Healthy : ModuleStatus.Warning,
            $"Wersja runtime: {version}", null,
            $"FrameworkDescription: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
            $"OSArchitecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\n" +
            $"ProcessArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}", null);
    }
    catch (Exception ex)
    {
        return new(".NET Runtime", ModuleStatus.Critical, "Nie mozna ustalic wersji runtime.", null, null, ex.ToString());
    }
}

static ModuleResult InspectEnvironment(string agentDirectory)
{
    try
    {
        var drive = new DriveInfo(Path.GetPathRoot(agentDirectory)!);
        var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        var status = freeGb < 1 ? ModuleStatus.Critical : freeGb < 5 ? ModuleStatus.Warning : ModuleStatus.Healthy;
        var details = $"Computer: {Environment.MachineName}\nUser: {Environment.UserDomainName}\\{Environment.UserName}\n" +
                      $"OS: {Environment.OSVersion}\n64-bit OS: {Environment.Is64BitOperatingSystem}\n" +
                      $"Processor count: {Environment.ProcessorCount}\nFree disk: {freeGb:F2} GB\nAgent directory: {agentDirectory}";
        return new("System / Environment", status, $"Wolne miejsce: {freeGb:F2} GB", agentDirectory, details, null);
    }
    catch (Exception ex)
    {
        return new("System / Environment", ModuleStatus.Critical, "Nie mozna odczytac danych systemowych.", agentDirectory, null, ex.ToString());
    }
}

static ModuleResult Missing(string name, string path, bool required) =>
    new(name, required ? ModuleStatus.Critical : ModuleStatus.Warning,
        required ? "Brak wymaganego pliku." : "Plik jeszcze nie zostal utworzony.", path, null, null);

static string BuildHtml(ModuleStatus overall, IReadOnlyList<ModuleResult> modules, string agentDirectory)
{
    static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    static string Css(ModuleStatus status) => status.ToString().ToLowerInvariant();
    static string Label(ModuleStatus status) => status switch
    {
        ModuleStatus.Healthy => "OK",
        ModuleStatus.Warning => "OSTRZEZENIE",
        _ => "KRYTYCZNY"
    };

    var rows = new StringBuilder();
    foreach (var module in modules)
    {
        rows.Append($"<details class='module {Css(module.Status)}'><summary><span class='dot'></span><strong>{E(module.Name)}</strong><span class='badge'>{Label(module.Status)}</span><span class='summary'>{E(module.Summary)}</span></summary><div class='body'>");
        if (!string.IsNullOrWhiteSpace(module.Path)) rows.Append($"<p><b>Sciezka:</b> <code>{E(module.Path)}</code></p>");
        if (!string.IsNullOrWhiteSpace(module.Details)) rows.Append($"<h3>Szczegoly</h3><pre>{E(module.Details)}</pre>");
        if (!string.IsNullOrWhiteSpace(module.Error)) rows.Append($"<h3>Blad</h3><pre class='error'>{E(module.Error)}</pre>");
        rows.Append("</div></details>");
    }

    return $"""
<!doctype html><html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SIRK Agent - raport stanu</title><style>
:root{{color-scheme:dark;--bg:#0b1220;--card:#111a2b;--text:#e8eef8;--muted:#9fb0c8;--ok:#24c875;--warn:#f0b429;--bad:#f05252;--line:#26344d}}
*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 Segoe UI,Arial,sans-serif}}main{{max-width:1200px;margin:auto;padding:28px}}
header{{display:flex;justify-content:space-between;gap:20px;align-items:center;margin-bottom:22px}}h1{{margin:0;font-size:28px}}.meta{{color:var(--muted)}}
.overall{{padding:10px 16px;border-radius:999px;font-weight:700}}.overall.healthy{{background:#123c2a;color:#7ff0b5}}.overall.warning{{background:#443615;color:#ffd66f}}.overall.critical{{background:#4b1f25;color:#ff9da7}}
.module{{background:var(--card);border:1px solid var(--line);border-left:5px solid var(--line);border-radius:12px;margin:10px 0;overflow:hidden}}.module.healthy{{border-left-color:var(--ok)}}.module.warning{{border-left-color:var(--warn)}}.module.critical{{border-left-color:var(--bad)}}
summary{{display:grid;grid-template-columns:14px minmax(180px,1fr) auto minmax(240px,2fr);gap:12px;align-items:center;padding:15px 18px;cursor:pointer}}summary:hover{{background:#172238}}.dot{{width:10px;height:10px;border-radius:50%;background:var(--line)}}.healthy .dot{{background:var(--ok)}}.warning .dot{{background:var(--warn)}}.critical .dot{{background:var(--bad)}}
.badge{{font-size:11px;font-weight:800;padding:3px 8px;border:1px solid var(--line);border-radius:999px}}.summary{{color:var(--muted)}}.body{{border-top:1px solid var(--line);padding:16px 18px}}pre{{white-space:pre-wrap;word-break:break-word;background:#07101e;padding:14px;border-radius:8px;max-height:520px;overflow:auto}}pre.error{{border:1px solid var(--bad)}}code{{color:#bdd8ff}}footer{{margin-top:22px;color:var(--muted)}}
@media(max-width:760px){{summary{{grid-template-columns:14px 1fr auto}}.summary{{grid-column:2/4}}header{{align-items:flex-start;flex-direction:column}}}}
</style></head><body><main><header><div><h1>SIRK Agent - raport stanu</h1><div class="meta">Urzadzenie: {E(Environment.MachineName)} | Wygenerowano: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}</div></div><div class="overall {Css(overall)}">STAN: {Label(overall)}</div></header>
<section>{rows}</section><footer>Katalog agenta: {E(agentDirectory)}. Sekcje mozna rozwijac, aby zobaczyc dane i pelne informacje o bledach.</footer></main></body></html>
""";
}

enum ModuleStatus { Healthy, Warning, Critical }
sealed record ModuleResult(string Name, ModuleStatus Status, string Summary, string? Path, string? Details, string? Error);
