namespace Sirk.Agent.Modules.Workspace;

internal interface IWorkspaceCaptureProvider
{
    bool IsAvailable { get; }

    string ProviderName { get; }

    WorkspaceCaptureResult Capture(CaptureFrameRequest request);
}

internal sealed record WorkspaceCaptureResult(
    bool Success,
    string? ContentType,
    byte[]? FrameBytes,
    string? ErrorCode,
    string? ErrorMessage);
