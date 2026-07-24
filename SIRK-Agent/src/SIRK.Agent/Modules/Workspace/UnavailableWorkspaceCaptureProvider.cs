namespace Sirk.Agent.Modules.Workspace;

internal sealed class UnavailableWorkspaceCaptureProvider : IWorkspaceCaptureProvider
{
    public bool IsAvailable => false;

    public string ProviderName => "unavailable";

    public WorkspaceCaptureResult Capture(CaptureFrameRequest request) =>
        new(
            false,
            null,
            null,
            "capture_provider_unavailable",
            $"Workspace capture provider is not installed for Windows session {request.SessionId}.");
}
