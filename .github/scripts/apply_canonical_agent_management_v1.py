from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path: str, value: str) -> None:
    (ROOT / path).write_text(value, encoding="utf-8", newline="\n")


def replace_once(value: str, old: str, new: str, label: str) -> str:
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one occurrence, found {count}")
    return value.replace(old, new, 1)


management_path = "src/SirkAgent.Service/ManagementWorker.cs"
management = read(management_path)
management = replace_once(
    management,
    '''        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps &&
            (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback))
        {
            _logger.LogWarning("Portal check-in endpoint is invalid.");
            return;
        }
''',
    '''        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var configuredEndpoint) ||
            configuredEndpoint.Scheme != Uri.UriSchemeHttps &&
            (configuredEndpoint.Scheme != Uri.UriSchemeHttp || !configuredEndpoint.IsLoopback))
        {
            _logger.LogWarning("Portal check-in endpoint is invalid.");
            return;
        }
        var endpoint = CanonicalAgentEndpoint(configuredEndpoint, "/api/v1/agent/checkin");
''',
    "canonical checkin endpoint",
)
management = replace_once(
    management,
    '''            if (!portalResponse.Ok)
                throw new InvalidDataException("Portal rejected the check-in.");
            new PortalPolicyDeliveryStore(paths.InboxDirectory, _json)
                .Store(TenantId, deviceId, portalResponse.Policies);
''',
    '''            if (!portalResponse.Ok)
                throw new InvalidDataException("Portal rejected the check-in.");
            SynchronizeTrustedPolicyKeys(paths.TrustedKeysPath, portalResponse.TrustedPolicyKeys);
            new PortalPolicyDeliveryStore(paths.InboxDirectory, _json)
                .Store(TenantId, deviceId, portalResponse.Policies);
            if (credential is not null &&
                !string.Equals(credential.Endpoint, endpoint.AbsoluteUri, StringComparison.Ordinal))
            {
                new PortalCredentialStore(paths.PortalCredentialPath,
                    new DpapiMachineStateProtector()).Save(
                    credential with { Endpoint = endpoint.AbsoluteUri });
            }
''',
    "trusted key sync and credential migration",
)
helper = r'''
    private void SynchronizeTrustedPolicyKeys(
        string path,
        IReadOnlyList<TrustedKeyEntry>? supplied)
    {
        if (supplied is not { Count: > 0 }) return;
        if (supplied.Count > 10)
            throw new InvalidDataException("Portal returned too many trusted policy keys.");

        var normalized = supplied.Select(ValidateTrustedPolicyKey)
            .OrderBy(value => value.KeyId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(value => value.KeyId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new InvalidDataException("Portal returned duplicate trusted policy key identifiers.");

        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<TrustedKeyDocument>(File.ReadAllBytes(path), _json)
                           ?? new TrustedKeyDocument([]);
            var current = existing.Keys.Select(ValidateTrustedPolicyKey)
                .OrderBy(value => value.KeyId, StringComparer.Ordinal)
                .ToArray();
            if (current.Length != normalized.Length ||
                current.Where((value, index) =>
                        !string.Equals(value.KeyId, normalized[index].KeyId, StringComparison.Ordinal) ||
                        !PublicKeysEqual(value.PublicKeyPem, normalized[index].PublicKeyPem))
                    .Any())
            {
                throw new InvalidDataException(
                    "Portal attempted to replace an established trusted policy key set.");
            }
            return;
        }

        AtomicFile.WriteJson(path, new TrustedKeyDocument(normalized), _json);
    }

    private static TrustedKeyEntry ValidateTrustedPolicyKey(TrustedKeyEntry value)
    {
        if (string.IsNullOrWhiteSpace(value.KeyId) || value.KeyId.Length > 128 ||
            string.IsNullOrWhiteSpace(value.PublicKeyPem))
            throw new InvalidDataException("Portal returned an invalid trusted policy key.");
        using var key = ECDsa.Create();
        key.ImportFromPem(value.PublicKeyPem);
        if (key.KeySize != 256)
            throw new InvalidDataException("Trusted policy key must use ECDSA P-256.");
        return new TrustedKeyEntry(value.KeyId.Trim(), key.ExportSubjectPublicKeyInfoPem());
    }

    private static bool PublicKeysEqual(string left, string right)
    {
        using var leftKey = ECDsa.Create();
        using var rightKey = ECDsa.Create();
        leftKey.ImportFromPem(left);
        rightKey.ImportFromPem(right);
        var leftBytes = leftKey.ExportSubjectPublicKeyInfo();
        var rightBytes = rightKey.ExportSubjectPublicKeyInfo();
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static Uri CanonicalAgentEndpoint(Uri source, string path) =>
        new UriBuilder(source) { Path = path, Query = string.Empty }.Uri;

'''
management = replace_once(
    management,
    '    private void WritePortalLoopDiagnostic(ManagementPaths paths, string stage) =>\n',
    helper + '    private void WritePortalLoopDiagnostic(ManagementPaths paths, string stage) =>\n',
    "trusted key helpers",
)
management = replace_once(
    management,
    '''internal sealed record PortalCheckInResponse(bool Ok, IReadOnlyList<JsonElement>? Policies,
    IReadOnlyList<PortalRemoteCommand>? Commands);
''',
    '''internal sealed record PortalCheckInResponse(
    bool Ok,
    IReadOnlyList<TrustedKeyEntry>? TrustedPolicyKeys,
    IReadOnlyList<JsonElement>? Policies,
    IReadOnlyList<PortalRemoteCommand>? Commands);
''',
    "checkin response trusted keys",
)
write(management_path, management)

# Direct desktop transport uses the same canonical Agent-facing route family.
desktop_path = "src/SirkAgent.Service/DesktopStreamWorker.cs"
desktop = read(desktop_path)
for old, new in (
    ('/api/agent/v1/desktop/frame', '/api/v1/agent/desktop/frame'),
    ('/api/agent/v1/desktop/stream', '/api/v1/agent/desktop/stream'),
    ('/api/agent/v1/desktop/control', '/api/v1/agent/desktop/control'),
):
    desktop = desktop.replace(old, new)
write(desktop_path, desktop)

# New installations enroll directly into the canonical ECDSA route.
setup_path = "src/SirkAgent.Setup/Program.cs"
setup = read(setup_path).replace(
    'portalOrigin + "/api/agent/v1/enroll"',
    'portalOrigin + "/api/v1/agent/enroll"')
write(setup_path, setup)

# CLI normalizes user-supplied Portal origins and old stored enrollment URLs.
cli_path = "src/SirkAgent.Cli/Program.cs"
cli = read(cli_path)
cli = replace_once(
    cli,
    '''    if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps &&
        (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback))
        throw new ArgumentException("Enrollment endpoint must use HTTPS (HTTP is allowed only for loopback testing).");
''',
    '''    if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var suppliedEndpoint) ||
        suppliedEndpoint.Scheme != Uri.UriSchemeHttps &&
        (suppliedEndpoint.Scheme != Uri.UriSchemeHttp || !suppliedEndpoint.IsLoopback))
        throw new ArgumentException("Enrollment endpoint must use HTTPS (HTTP is allowed only for loopback testing).");
    var endpoint = CanonicalAgentEndpoint(suppliedEndpoint, "/api/v1/agent/enroll");
''',
    "CLI enrollment endpoint normalization",
)
cli = replace_once(
    cli,
    '''    var checkInEndpoint = string.IsNullOrWhiteSpace(enrollment.CheckInEndpoint)
        ? new Uri(endpoint, "/api/agent/v1/checkin")
        : new Uri(endpoint, enrollment.CheckInEndpoint);
''',
    '''    var checkInEndpoint = string.IsNullOrWhiteSpace(enrollment.CheckInEndpoint)
        ? CanonicalAgentEndpoint(endpoint, "/api/v1/agent/checkin")
        : CanonicalAgentEndpoint(new Uri(endpoint, enrollment.CheckInEndpoint),
            "/api/v1/agent/checkin");
''',
    "CLI checkin endpoint normalization",
)
cli = replace_once(
    cli,
    '''static string? GetOption(string[] values, string name)
''',
    '''static Uri CanonicalAgentEndpoint(Uri source, string path) =>
    new UriBuilder(source) { Path = path, Query = string.Empty }.Uri;

static string? GetOption(string[] values, string name)
''',
    "CLI canonical endpoint helper",
)
write(cli_path, cli)

# Permanent contract: no old route and safe trust-on-first-authenticated-checkin behavior.
contract_path = ROOT / "tests/canonical-agent-management-v1-contract.ps1"
contract_path.write_text(r'''#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceFiles = Get-ChildItem (Join-Path $root 'src') -Recurse -File -Include *.cs
$legacy = $sourceFiles | Select-String -SimpleMatch '/api/agent/v1/'
if ($legacy) {
    $legacy | ForEach-Object { Write-Error $_ }
    throw 'Legacy Agent management route remains in product source.'
}

$management = Get-Content (Join-Path $root 'src\SirkAgent.Service\ManagementWorker.cs') -Raw
foreach ($required in @(
    '/api/v1/agent/checkin',
    'SynchronizeTrustedPolicyKeys',
    'Portal attempted to replace an established trusted policy key set',
    'PublicKeysEqual',
    'TrustedPolicyKeys',
    'credential with { Endpoint = endpoint.AbsoluteUri }'
)) {
    if (-not $management.Contains($required)) {
        throw "Canonical Agent management contract is missing: $required"
    }
}

$desktop = Get-Content (Join-Path $root 'src\SirkAgent.Service\DesktopStreamWorker.cs') -Raw
foreach ($required in @(
    '/api/v1/agent/desktop/stream',
    '/api/v1/agent/desktop/control'
)) {
    if (-not $desktop.Contains($required)) {
        throw "Canonical desktop Agent endpoint is missing: $required"
    }
}

$setup = Get-Content (Join-Path $root 'src\SirkAgent.Setup\Program.cs') -Raw
if (-not $setup.Contains('/api/v1/agent/enroll')) {
    throw 'Setup still enrolls through a non-canonical route.'
}

Write-Host 'CANONICAL_AGENT_MANAGEMENT_V1_CONTRACT_OK'
''', encoding="utf-8", newline="\n")

# Add contract to both permanent CI gates.
for workflow_path, marker in (
    (".github/workflows/dotnet10-contract.yml",
     "          & pwsh -NoProfile -File tests/shared-updater-installer-contract.ps1\n"),
    (".github/workflows/product-test-readiness.yml",
     "          & pwsh -NoProfile -File tests/shared-updater-installer-contract.ps1\n"),
):
    workflow = read(workflow_path)
    addition = (
        "          & pwsh -NoProfile -File tests/canonical-agent-management-v1-contract.ps1\n"
        "          if ($LASTEXITCODE -ne 0) { throw 'Canonical Agent management v1 contract failed.' }\n"
    )
    workflow = replace_once(workflow, marker, addition + marker,
                            f"canonical contract in {workflow_path}")
    write(workflow_path, workflow)

# Ensure product source contains no old management route.
for path in (ROOT / "src").rglob("*.cs"):
    if "/api/agent/v1/" in path.read_text(encoding="utf-8-sig"):
        raise RuntimeError(f"legacy Agent management route remains: {path.relative_to(ROOT)}")

print("Canonical Agent management v1 applied to Agent.")
