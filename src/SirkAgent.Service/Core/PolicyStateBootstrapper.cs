using SirkAgent.Policy;

namespace SirkAgent.Service.Core;

internal sealed record PolicyBootstrapResult(
    bool Created,
    string Code,
    string Message,
    string Path);

internal static class PolicyStateBootstrapper
{
    public static PolicyBootstrapResult EnsureInitialized(string path, IPolicyStateStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(store);

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return new PolicyBootstrapResult(
                false,
                "POLICY_STATE_EXISTING",
                "Existing policy state was preserved for integrity validation.",
                fullPath);
        }

        store.Save(PolicyState.Empty);
        return new PolicyBootstrapResult(
            true,
            "POLICY_STATE_INITIALIZED",
            "A new DPAPI-protected empty policy state was created for first startup.",
            fullPath);
    }
}
