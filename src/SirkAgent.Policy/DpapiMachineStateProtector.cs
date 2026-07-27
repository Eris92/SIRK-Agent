using System.Security.Cryptography;

namespace SirkAgent.Policy;

public sealed class DpapiMachineStateProtector : IStateProtector
{
    private readonly byte[]? _optionalEntropy;

    public DpapiMachineStateProtector(byte[]? optionalEntropy = null)
    {
        _optionalEntropy = optionalEntropy?.ToArray();
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        EnsureWindows();
        return ProtectedData.Protect(
            plaintext.ToArray(),
            _optionalEntropy,
            DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        EnsureWindows();
        return ProtectedData.Unprotect(
            protectedData.ToArray(),
            _optionalEntropy,
            DataProtectionScope.LocalMachine);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI machine protection is available only on Windows.");
    }
}
