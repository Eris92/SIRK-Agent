using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirk.Agent.Modules.Workspace;

internal sealed class WorkspaceHostCaptureProvider(IWorkspaceHostLauncher launcher) : IWorkspaceCaptureProvider
{
    private const int MaximumControlMessageBytes = 64 * 1024;
    private const int MaximumFrameMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public bool IsAvailable => launcher.IsSupported && File.Exists(WorkspaceHostPath);

    public string ProviderName => "WorkspaceHost.GDI.JPEG";

    private static string WorkspaceHostPath => Path.Combine(AppContext.BaseDirectory, "SIRK-WorkspaceHost.exe");

    public WorkspaceCaptureResult Capture(CaptureFrameRequest request)
    {
        if (!IsAvailable)
        {
            return Failure("capture_provider_unavailable", "SIRK-WorkspaceHost is not installed next to SIRK-Agent.");
        }

        try
        {
            return CaptureAsync(request).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return Failure("capture_timeout", "WorkspaceHost did not complete capture within the allowed time.");
        }
        catch (IOException)
        {
            return Failure("workspace_host_ipc_failed", "WorkspaceHost IPC failed safely.");
        }
        catch (JsonException)
        {
            return Failure("workspace_host_response_invalid", "WorkspaceHost returned invalid JSON.");
        }
        catch (InvalidDataException exception)
        {
            return Failure("workspace_host_response_invalid", exception.Message);
        }
    }

    private async Task<WorkspaceCaptureResult> CaptureAsync(CaptureFrameRequest request)
    {
        using var timeout = new CancellationTokenSource(OperationTimeout);
        using NamedPipeServerStream pipe = CreatePipe(request.SessionId, out string pipeName);

        WorkspaceHostLaunchResult launch = launcher.Launch((uint)request.SessionId, WorkspaceHostPath, pipeName);
        if (!launch.Success || launch.ProcessId is null || launch.Token is null)
        {
            return Failure(launch.ErrorCode ?? "workspace_host_launch_failed", launch.ErrorMessage ?? "WorkspaceHost launch failed safely.");
        }

        await pipe.WaitForConnectionAsync(timeout.Token);
        byte[] helloBytes = await ReadFrameAsync(pipe, MaximumControlMessageBytes, timeout.Token);
        WorkspaceHostHello? hello = JsonSerializer.Deserialize<WorkspaceHostHello>(helloBytes, JsonOptions);
        if (!ValidateHello(hello, launch, request.SessionId))
        {
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { ok = false }, JsonOptions), MaximumControlMessageBytes, timeout.Token);
            return Failure("workspace_host_handshake_rejected", "WorkspaceHost identity or one-time token validation failed.");
        }

        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { ok = true }, JsonOptions), MaximumControlMessageBytes, timeout.Token);
        byte[] command = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "WorkspaceHost.CaptureFrame",
            request = new
            {
                monitorId = request.MonitorId,
                quality = request.Quality,
                maxWidth = request.MaxWidth,
                maxHeight = request.MaxHeight,
                includeCursor = request.IncludeCursor
            }
        }, JsonOptions);
        await WriteFrameAsync(pipe, command, MaximumControlMessageBytes, timeout.Token);

        byte[] responseBytes = await ReadFrameAsync(pipe, MaximumFrameMessageBytes, timeout.Token);
        WorkspaceHostResponse? response = JsonSerializer.Deserialize<WorkspaceHostResponse>(responseBytes, JsonOptions);
        if (response is null)
        {
            return Failure("workspace_host_response_invalid", "WorkspaceHost returned an empty response.");
        }

        if (!response.Ok)
        {
            return Failure(response.Error?.Code ?? "capture_failed", response.Error?.Message ?? "WorkspaceHost capture failed safely.");
        }

        if (!string.Equals(response.ContentType, "image/jpeg", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(response.FrameBase64))
        {
            return Failure("workspace_host_response_invalid", "WorkspaceHost response did not contain a JPEG frame.");
        }

        byte[] frame;
        try
        {
            frame = Convert.FromBase64String(response.FrameBase64);
        }
        catch (FormatException)
        {
            return Failure("workspace_host_response_invalid", "WorkspaceHost frame was not valid Base64.");
        }

        if (frame.Length is <= 0 or > 12 * 1024 * 1024)
        {
            return Failure("frame_size_invalid", "WorkspaceHost frame size is outside the allowed limit.");
        }

        return new WorkspaceCaptureResult(true, "image/jpeg", frame, null, null);
    }

    private static NamedPipeServerStream CreatePipe(int sessionId, out string pipeName)
    {
        pipeName = $"SIRK.WorkspaceHost.{sessionId}.{Guid.NewGuid():N}";
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddRule(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            MaximumControlMessageBytes,
            MaximumFrameMessageBytes,
            security,
            HandleInheritability.None,
            PipeAccessRights.ChangePermissions);
    }

    private static void AddRule(PipeSecurity security, IdentityReference identity)
    {
        const PipeAccessRights rights = PipeAccessRights.ReadData | PipeAccessRights.WriteData |
                                        PipeAccessRights.ReadAttributes | PipeAccessRights.WriteAttributes |
                                        PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));
    }

    private static bool ValidateHello(WorkspaceHostHello? hello, WorkspaceHostLaunchResult launch, int expectedSessionId)
    {
        if (hello is null || hello.ProtocolVersion != 1 || hello.MessageType != "WorkspaceHost.Hello" ||
            hello.SessionId != expectedSessionId || hello.ProcessId != launch.ProcessId ||
            string.IsNullOrEmpty(hello.Token) || string.IsNullOrEmpty(launch.Token))
        {
            return false;
        }

        byte[] actual = Encoding.UTF8.GetBytes(hello.Token);
        byte[] expected = Encoding.UTF8.GetBytes(launch.Token);
        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return false;
        }

        return ProcessIdToSessionId((uint)hello.ProcessId, out uint actualSessionId) && actualSessionId == (uint)expectedSessionId;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, int maximumBytes, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 || payload.Length > maximumBytes)
        {
            throw new InvalidDataException("WorkspaceHost IPC payload exceeds the allowed limit.");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException("WorkspaceHost IPC frame length is outside the allowed limit.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static WorkspaceCaptureResult Failure(string code, string message) => new(false, null, null, code, message);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    private sealed record WorkspaceHostHello
    {
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; init; }
        [JsonPropertyName("messageType")]
        public string MessageType { get; init; } = string.Empty;
        [JsonPropertyName("sessionId")]
        public int SessionId { get; init; }
        [JsonPropertyName("processId")]
        public int ProcessId { get; init; }
        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;
    }

    private sealed record WorkspaceHostResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }
        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }
        [JsonPropertyName("frameBase64")]
        public string? FrameBase64 { get; init; }
        [JsonPropertyName("error")]
        public WorkspaceHostError? Error { get; init; }
    }

    private sealed record WorkspaceHostError
    {
        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;
        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }
}