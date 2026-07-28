using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SirkAgent.Service;

internal sealed class ControlFileWorker : BackgroundService
{
    private readonly ILogger<ControlFileWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ControlFileWorker(ILogger<ControlFileWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        var requestPath = Path.Combine(root, "control-request.json");
        var responsePath = Path.Combine(root, "control-response.json");
        Directory.CreateDirectory(root);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(requestPath))
                {
                    var request = JsonSerializer.Deserialize<ControlRequest>(await File.ReadAllBytesAsync(requestPath, stoppingToken), _json);
                    if (request is not null && DateTimeOffset.UtcNow - request.TimestampUtc < TimeSpan.FromMinutes(2))
                    {
                        object payload = request.Command.ToLowerInvariant() switch
                        {
                            "status" => new
                            {
                                ok = true,
                                requestId = request.RequestId,
                                management = ReadJson(Path.Combine(root, "management-state.json")),
                                heartbeat = ReadJson(Path.Combine(root, "heartbeat-latest.json")),
                                security = ReadJson(Path.Combine(root, "security-state.json"))
                            },
                            "process" => new { ok = true, requestId = request.RequestId, accepted = true, note = "Inbox will be processed within 15 seconds." },
                            "flush" => new { ok = true, requestId = request.RequestId, accepted = true, note = "Telemetry flush will run within 15 seconds." },
                            "sync" => new { ok = true, requestId = request.RequestId, accepted = true, note = "Portal sync will run within 15 seconds." },
                            _ => new { ok = false, requestId = request.RequestId, error = "Unsupported command." }
                        };
                        await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(payload, _json), stoppingToken);
                    }
                    File.Delete(requestPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Control file request failed.");
            }

            await Task.Delay(500, stoppingToken);
        }
    }

    private static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }
}

internal sealed record ControlRequest(Guid RequestId, DateTimeOffset TimestampUtc, string Command);
