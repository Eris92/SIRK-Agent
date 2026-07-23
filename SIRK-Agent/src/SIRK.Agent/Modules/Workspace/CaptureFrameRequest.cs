using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirk.Agent.Modules.Workspace;

internal sealed record CaptureFrameRequest
{
    internal const int MinimumQuality = 20;
    internal const int MaximumQuality = 95;
    internal const int MinimumWidth = 320;
    internal const int MaximumWidth = 7680;
    internal const int MinimumHeight = 200;
    internal const int MaximumHeight = 4320;

    [JsonPropertyName("sessionId")]
    public int SessionId { get; init; }

    [JsonPropertyName("monitorId")]
    public string MonitorId { get; init; } = "primary";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "jpeg";

    [JsonPropertyName("quality")]
    public int Quality { get; init; } = 70;

    [JsonPropertyName("maxWidth")]
    public int MaxWidth { get; init; } = 1920;

    [JsonPropertyName("maxHeight")]
    public int MaxHeight { get; init; } = 1080;

    [JsonPropertyName("includeCursor")]
    public bool IncludeCursor { get; init; } = true;

    internal static bool TryParse(JsonElement payload, out CaptureFrameRequest? request, out string error)
    {
        request = null;
        error = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "payload must be a JSON object.";
            return false;
        }

        try
        {
            request = payload.Deserialize<CaptureFrameRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = false
            });
        }
        catch (JsonException)
        {
            error = "payload contains invalid Workspace.CaptureFrame values.";
            return false;
        }

        if (request is null)
        {
            error = "payload is required.";
            return false;
        }

        if (request.SessionId < 0)
        {
            error = "sessionId must be zero or a positive Windows session identifier.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.MonitorId) || request.MonitorId.Length > 128)
        {
            error = "monitorId is required and limited to 128 characters.";
            return false;
        }

        if (!string.Equals(request.Format, "jpeg", StringComparison.Ordinal))
        {
            error = "format must be jpeg in SIRK Protocol v1.";
            return false;
        }

        if (request.Quality is < MinimumQuality or > MaximumQuality)
        {
            error = $"quality must be between {MinimumQuality} and {MaximumQuality}.";
            return false;
        }

        if (request.MaxWidth is < MinimumWidth or > MaximumWidth)
        {
            error = $"maxWidth must be between {MinimumWidth} and {MaximumWidth}.";
            return false;
        }

        if (request.MaxHeight is < MinimumHeight or > MaximumHeight)
        {
            error = $"maxHeight must be between {MinimumHeight} and {MaximumHeight}.";
            return false;
        }

        return true;
    }
}
