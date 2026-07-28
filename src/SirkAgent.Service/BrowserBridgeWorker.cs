using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed record BrowserBridgePolicy(
    bool Enabled,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlySet<string> AllowedEvents,
    string Mode,
    string? CaseId,
    DateTimeOffset ExpiresAtUtc)
{
    public bool Authorized => Enabled && Mode is "Investigation" or "InsiderRisk" &&
                              !string.IsNullOrWhiteSpace(CaseId) &&
                              ExpiresAtUtc > DateTimeOffset.UtcNow;
}

internal sealed record BrowserBridgeDecision(bool Accepted, string Code, string? Domain);

internal sealed class BrowserBridgeWorker : BackgroundService
{
    private const string TenantId = "investa";
    private const string PipeName = "SIRK-Agent-Browser-Bridge";
    private readonly ILogger<BrowserBridgeWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public BrowserBridgeWorker(ILogger<BrowserBridgeWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = AgentPaths.CreateDefault();
        paths.EnsureDirectories();
        var policyPath = Path.Combine(paths.AgentDirectory, "active-policy.json");
        var latestPath = Path.Combine(paths.AgentDirectory, "browser-activity-latest.json");
        var protector = new DpapiMachineStateProtector();
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, protector).LoadOrCreate(TenantId);
        var telemetry = new TelemetryQueue(paths.TelemetryQueueDirectory, protector,
            50L * 1024 * 1024, _json);
        var evidence = new EvidenceChain(paths.EvidenceLogPath, paths.EvidenceStatePath, protector, _json);

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var clientSid = ClientSid(pipe);
                var activeSid = ActiveConsoleUserSid();
                if (clientSid is null || activeSid is null ||
                    !string.Equals(clientSid, activeSid, StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(pipe, false, "BROWSER_CLIENT_SID_DENIED", stoppingToken);
                    continue;
                }

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                var line = await reader.ReadLineAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(line) || line.Length > 256 * 1024)
                {
                    await RespondAsync(pipe, false, "BROWSER_MESSAGE_INVALID", stoppingToken);
                    continue;
                }
                using var document = JsonDocument.Parse(line);
                var policy = ReadPolicy(policyPath);
                var decision = Evaluate(policy, document.RootElement);
                if (!decision.Accepted)
                {
                    await RespondAsync(pipe, false, decision.Code, stoppingToken);
                    continue;
                }

                var activity = new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    policy.Mode,
                    policy.CaseId,
                    sourceSid = clientSid,
                    domain = decision.Domain,
                    browserEvent = Sanitize(document.RootElement)
                };
                AtomicFile.WriteJson(latestPath, activity, _json);
                telemetry.Enqueue("Browser", "Activity", TelemetryPriority.High, activity);
                evidence.Append(TenantId, identity.DeviceId, "Browser", "Activity", activity);
                await RespondAsync(pipe, true, "BROWSER_EVENT_ACCEPTED", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                _logger.LogError(error, "Browser Bridge request failed.");
                if (pipe.IsConnected)
                    await RespondAsync(pipe, false, "BROWSER_BRIDGE_ERROR", CancellationToken.None);
            }
        }
    }

    internal static BrowserBridgePolicy ReadPolicy(string path)
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
            var expiry = root.TryGetProperty("expiresAtUtc", out var expiryValue) &&
                         expiryValue.TryGetDateTimeOffset(out var value) ? value : DateTimeOffset.MinValue;
            if (!root.TryGetProperty("settings", out var settings) ||
                !settings.TryGetProperty("browserBridge", out var bridge) ||
                bridge.ValueKind != JsonValueKind.Object)
                return Disabled() with { Mode = mode, CaseId = caseId, ExpiresAtUtc = expiry };
            var enabled = bridge.TryGetProperty("enabled", out var enabledValue) &&
                          enabledValue.ValueKind == JsonValueKind.True;
            var domains = Strings(bridge, "allowedDomains", 500)
                .Select(NormalizeDomain).Where(domain => domain is not null).Select(domain => domain!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var events = Strings(bridge, "allowedEvents", 20)
                .Where(value => AllowedEventTypes.Contains(value)).ToHashSet(StringComparer.Ordinal);
            return new BrowserBridgePolicy(enabled, domains, events, mode, caseId, expiry);
        }
        catch { return Disabled(); }
    }

    internal static BrowserBridgeDecision Evaluate(BrowserBridgePolicy policy, JsonElement message)
    {
        if (!policy.Authorized)
            return new(false, "BROWSER_POLICY_NOT_AUTHORIZED", null);
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("type", out var typeValue) ||
            typeValue.ValueKind != JsonValueKind.String)
            return new(false, "BROWSER_EVENT_TYPE_INVALID", null);
        var type = typeValue.GetString() ?? "";
        if (!policy.AllowedEvents.Contains(type))
            return new(false, "BROWSER_EVENT_NOT_ALLOWED", null);
        if (!message.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return new(false, "BROWSER_URL_INVALID", null);
        var domain = NormalizeDomain(uri.IdnHost);
        var allowed = domain is not null && policy.AllowedDomains.Any(candidate =>
            string.Equals(domain, candidate, StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase));
        return allowed ? new(true, "BROWSER_EVENT_ACCEPTED", domain)
            : new(false, "BROWSER_DOMAIN_NOT_ALLOWED", domain);
    }

    private static object Sanitize(JsonElement message)
    {
        var type = message.GetProperty("type").GetString() ?? "";
        var url = message.GetProperty("url").GetString() ?? "";
        string? Text(string name, int max)
        {
            if (!message.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            var text = value.GetString();
            return text is null || text.Length <= max ? text : text[..max];
        }
        object[]? files = null;
        if (message.TryGetProperty("files", out var fileValues) && fileValues.ValueKind == JsonValueKind.Array)
            files = fileValues.EnumerateArray().Take(100).Select(file => new
            {
                name = SafeFileName(file, "name"),
                extension = SafeText(file, "extension", 32),
                mime = SafeText(file, "mime", 128),
                bytes = file.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var length)
                    ? Math.Clamp(length, 0, 1L << 50) : 0
            } as object).ToArray();
        return new
        {
            type,
            url = url.Length <= 4096 ? url : url[..4096],
            title = Text("title", 512),
            transitionType = Text("transitionType", 64),
            method = Text("method", 16),
            requestId = Text("requestId", 128),
            statusCode = message.TryGetProperty("statusCode", out var status) && status.TryGetInt32(out var statusCode)
                ? Math.Clamp(statusCode, 0, 999) : 0,
            ok = message.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            error = Text("error", 256),
            fileName = SafeFileName(message, "fileName"),
            mime = Text("mime", 128),
            bytes = message.TryGetProperty("bytes", out var total) && total.TryGetInt64(out var size)
                ? Math.Clamp(size, 0, 1L << 50) : 0,
            files
        };
    }

    private static string? SafeFileName(JsonElement value, string name)
    {
        var text = SafeText(value, name, 260);
        return string.IsNullOrWhiteSpace(text) ? null : Path.GetFileName(text);
    }

    private static string? SafeText(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        var text = property.GetString();
        return text is null || text.Length <= maximum ? text : text[..maximum];
    }

    private static readonly HashSet<string> AllowedEventTypes =
        ["tab", "navigation", "download", "uploadSelection", "dragDrop", "formSubmit", "uploadResult"];

    private static BrowserBridgePolicy Disabled() =>
        new(false, [], new HashSet<string>(), "Normal", null, DateTimeOffset.MinValue);

    private static IEnumerable<string> Strings(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).Take(maximum);
    }

    private static string? NormalizeDomain(string? value)
    {
        value = value?.Trim().Trim('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) || value.Length > 253 ? null : value;
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256 * 1024, 256 * 1024, security);
    }

    private static string? ClientSid(NamedPipeServerStream pipe)
    {
        string? sid = null;
        pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value);
        return sid;
    }

    private static string? ActiveConsoleUserSid()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue || !WTSQueryUserToken(sessionId, out var token))
            return null;
        using var handle = new SafeAccessTokenHandle(token);
        using var identity = new WindowsIdentity(handle.DangerousGetHandle());
        return identity.User?.Value;
    }

    private static async Task RespondAsync(Stream pipe, bool ok, string code, CancellationToken token)
    {
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
            { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok, code }).AsMemory(), token);
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
}
