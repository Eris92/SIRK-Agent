using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace SirkAgent.Service.Core;

internal enum HostDirectoryKind
{
    Workgroup,
    ActiveDirectory,
    MicrosoftEntra,
    Hybrid
}

internal enum ManagementCheckStatus
{
    Healthy,
    Warning,
    Critical,
    NotApplicable,
    NotConfigured
}

internal sealed record CommandResult(int ExitCode, string Output, string Error);

internal interface ICommandProbe
{
    Task<CommandResult> RunAsync(string fileName, string arguments, TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProcessCommandProbe : ICommandProbe
{
    public async Task<CommandResult> RunAsync(string fileName, string arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new CommandResult(-1, await output, "Command timed out.");
        }
        return new CommandResult(process.ExitCode, await output, await error);
    }
}

internal sealed record ManagementPlaneCheck(
    string Id,
    ManagementCheckStatus Status,
    string Summary,
    string? Problem,
    string? Remediation,
    bool RepairAttempted,
    bool RepairSucceeded);

internal sealed record ManagementPlaneSnapshot(
    DateTimeOffset TimestampUtc,
    HostDirectoryKind DirectoryKind,
    string? DomainName,
    string? EntraTenantId,
    string? EntraDeviceId,
    bool DomainJoined,
    bool AzureAdJoined,
    bool EnterpriseJoined,
    IReadOnlyList<ManagementPlaneCheck> Checks)
{
    public bool Healthy => Checks.All(check => check.Status is ManagementCheckStatus.Healthy
        or ManagementCheckStatus.NotApplicable);
}

internal sealed record ManagementPlaneRequirements(
    IReadOnlyList<string> RequiredAppliedGpos,
    bool RequireDefender,
    bool RequireFirewall,
    bool RequireBitLocker,
    bool RequireSecureBoot,
    bool RequireTpm,
    bool AllowSafeRepair,
    EntraPolicySnapshot? EntraPolicy)
{
    public static ManagementPlaneRequirements Default { get; } =
        new([], true, true, true, true, true, false, null);
}

internal sealed record EntraPolicySnapshot(
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<EntraPolicyState> RequiredPolicies);

internal sealed record EntraPolicyState(string Id, string DisplayName, bool Enabled, bool Present);

internal sealed class ManagementPlaneHealth
{
    private readonly ICommandProbe _probe;

    public ManagementPlaneHealth(ICommandProbe probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<ManagementPlaneSnapshot> InspectAsync(ManagementPlaneRequirements requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var dsreg = await _probe.RunAsync("dsregcmd.exe", "/status", TimeSpan.FromSeconds(20),
            cancellationToken);
        var domainJoined = ParseYes(dsreg.Output, "DomainJoined");
        var azureAdJoined = ParseYes(dsreg.Output, "AzureAdJoined");
        var enterpriseJoined = ParseYes(dsreg.Output, "EnterpriseJoined");
        var domainName = ParseValue(dsreg.Output, "DomainName");
        var tenantId = ParseValue(dsreg.Output, "TenantId");
        var deviceId = ParseValue(dsreg.Output, "DeviceId");
        var kind = Classify(domainJoined, azureAdJoined);
        var checks = new List<ManagementPlaneCheck>();

        checks.Add(dsreg.ExitCode == 0
            ? Check("directory-registration", ManagementCheckStatus.Healthy,
                $"Host classification: {kind}.")
            : Check("directory-registration", ManagementCheckStatus.Critical,
                "Unable to inspect Windows directory registration.", dsreg.Error,
                "Run dsregcmd /status as LocalSystem or an administrator and inspect DeviceReg diagnostics."));

        if (domainJoined)
        {
            var secureChannel = await _probe.RunAsync("nltest.exe", "/sc_verify:" + domainName,
                TimeSpan.FromSeconds(30), cancellationToken);
            var channelHealthy = secureChannel.ExitCode == 0;
            var attempted = false;
            var repaired = false;
            if (!channelHealthy && requirements.AllowSafeRepair)
            {
                attempted = true;
                var rediscover = await _probe.RunAsync("nltest.exe", "/sc_reset:" + domainName,
                    TimeSpan.FromSeconds(45), cancellationToken);
                repaired = rediscover.ExitCode == 0;
            }
            checks.Add(Check("ad-secure-channel",
                channelHealthy || repaired ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
                channelHealthy ? "Active Directory secure channel is healthy."
                    : repaired ? "Active Directory secure channel was repaired."
                    : "Active Directory secure channel validation failed.",
                channelHealthy || repaired ? null : secureChannel.Error + secureChannel.Output,
                "Verify DNS points to domain controllers, time synchronization and the computer account.",
                attempted, repaired));

            var gpresult = await _probe.RunAsync("gpresult.exe", "/scope computer /r",
                TimeSpan.FromSeconds(60), cancellationToken);
            checks.Add(Check("ad-resultant-policy",
                gpresult.ExitCode == 0 ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
                gpresult.ExitCode == 0 ? "Computer Resultant Set of Policy is available."
                    : "Computer Resultant Set of Policy could not be generated.",
                gpresult.ExitCode == 0 ? null : gpresult.Error + gpresult.Output,
                "Run gpupdate /target:computer /force, then inspect GroupPolicy Operational events."));
            if (gpresult.ExitCode == 0)
            {
                foreach (var requiredGpo in requirements.RequiredAppliedGpos)
                {
                    var present = gpresult.Output.Contains(requiredGpo, StringComparison.OrdinalIgnoreCase);
                    checks.Add(Check("ad-gpo:" + requiredGpo,
                        present ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
                        present ? $"Required GPO is applied: {requiredGpo}."
                            : $"Required GPO is missing: {requiredGpo}.",
                        present ? null : "The required GPO was not found in computer RSoP.",
                        "Verify security filtering, WMI filters, OU linkage and replication; then run gpupdate /target:computer /force."));
                }
            }
        }
        else
        {
            checks.Add(Check("ad-secure-channel", ManagementCheckStatus.NotApplicable,
                "Host is not joined to Active Directory."));
            checks.Add(Check("ad-resultant-policy", ManagementCheckStatus.NotApplicable,
                "Host is not joined to Active Directory."));
        }

        if (azureAdJoined)
        {
            checks.Add(string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(deviceId)
                ? Check("entra-device-registration", ManagementCheckStatus.Critical,
                    "Microsoft Entra registration identifiers are incomplete.",
                    "TenantId or DeviceId is missing from dsregcmd output.",
                    "Inspect Microsoft-Windows-User Device Registration/Admin events and device registration.")
                : Check("entra-device-registration", ManagementCheckStatus.Healthy,
                    "Microsoft Entra device registration is present."));
            if (requirements.EntraPolicy is null)
            {
                checks.Add(Check("entra-central-policy", ManagementCheckStatus.NotConfigured,
                    "Central Microsoft Entra policy verification requires a Portal-issued read-only snapshot.",
                    "No central policy snapshot was supplied in the signed device policy.",
                    "Configure the Portal connector with Policy.Read.All and send expected policy identifiers in a signed device policy."));
            }
            else
            {
                var stale = DateTimeOffset.UtcNow - requirements.EntraPolicy.RetrievedAtUtc > TimeSpan.FromHours(24);
                checks.Add(Check("entra-central-policy-freshness",
                    stale ? ManagementCheckStatus.Critical : ManagementCheckStatus.Healthy,
                    stale ? "Microsoft Entra policy snapshot is stale." : "Microsoft Entra policy snapshot is current.",
                    stale ? $"Snapshot timestamp: {requirements.EntraPolicy.RetrievedAtUtc:O}." : null,
                    stale ? "Refresh Conditional Access state in SIRK Portal using the read-only Graph connector." : null));
                foreach (var policy in requirements.EntraPolicy.RequiredPolicies)
                {
                    var healthy = policy.Present && policy.Enabled;
                    checks.Add(Check("entra-policy:" + policy.Id,
                        healthy ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
                        healthy ? $"Required Entra policy is enabled: {policy.DisplayName}."
                            : $"Required Entra policy is missing or disabled: {policy.DisplayName}.",
                        healthy ? null : policy.Present ? "Policy exists but is disabled." : "Policy was not found.",
                        "Review the policy in Conditional Access. Enable or create it only through an approved change."));
                }
            }
        }
        else
        {
            checks.Add(Check("entra-device-registration", ManagementCheckStatus.NotApplicable,
                "Host is not Microsoft Entra joined."));
            checks.Add(Check("entra-central-policy", ManagementCheckStatus.NotApplicable,
                "Host is not Microsoft Entra joined."));
        }

        await AddLocalBaselineChecksAsync(checks, requirements, cancellationToken);
        return new ManagementPlaneSnapshot(DateTimeOffset.UtcNow, kind, domainName, tenantId, deviceId,
            domainJoined, azureAdJoined, enterpriseJoined, checks);
    }

    private async Task AddLocalBaselineChecksAsync(List<ManagementPlaneCheck> checks,
        ManagementPlaneRequirements requirements, CancellationToken cancellationToken)
    {
        if (requirements.RequireFirewall)
        {
            var firewall = await _probe.RunAsync("netsh.exe", "advfirewall show allprofiles state",
                TimeSpan.FromSeconds(20), cancellationToken);
            var states = Regex.Matches(firewall.Output, @"(?im)^\s*(?:State|Stan)\s+(?<value>ON|OFF|WŁĄCZONE|WYŁĄCZONE)\s*$")
                .Select(match => match.Groups["value"].Value).ToArray();
            var healthy = firewall.ExitCode == 0 && states.Length >= 3 &&
                          states.All(value => value is "ON" or "WŁĄCZONE");
            checks.Add(Check("baseline-firewall", healthy ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
                healthy ? "Windows Firewall is enabled for all profiles." : "Windows Firewall is not enabled for every profile.",
                healthy ? null : firewall.Error + firewall.Output,
                "Enable Domain, Private and Public firewall profiles through the authoritative policy."));
        }

        if (requirements.RequireDefender)
        {
            const string command = "-NoProfile -NonInteractive -Command \"$s=Get-MpComputerStatus;" +
                                   "if($s.AntivirusEnabled -and $s.RealTimeProtectionEnabled){exit 0}else{exit 3}\"";
            var defender = await _probe.RunAsync("powershell.exe", command, TimeSpan.FromSeconds(30),
                cancellationToken);
            checks.Add(Check("baseline-defender",
                defender.ExitCode == 0 ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
                defender.ExitCode == 0 ? "Microsoft Defender Antivirus and real-time protection are enabled."
                    : "Microsoft Defender Antivirus or real-time protection is disabled.",
                defender.ExitCode == 0 ? null : defender.Error,
                "Restore Defender through tamper-protected security policy; inspect passive mode and third-party AV."));
        }

        await AddBooleanPowerShellCheck(checks, requirements.RequireBitLocker, "baseline-bitlocker",
            "(Get-BitLockerVolume -MountPoint $env:SystemDrive).ProtectionStatus -eq 'On'",
            "BitLocker protection is enabled.", "BitLocker protection is not enabled.",
            "Enable BitLocker using the organization recovery-key escrow procedure.", cancellationToken);
        await AddBooleanPowerShellCheck(checks, requirements.RequireSecureBoot, "baseline-secure-boot",
            "Confirm-SecureBootUEFI", "Secure Boot is enabled.", "Secure Boot is not enabled or unavailable.",
            "Enable UEFI Secure Boot after validating firmware and recovery-key readiness.", cancellationToken);
        await AddBooleanPowerShellCheck(checks, requirements.RequireTpm, "baseline-tpm",
            "(Get-Tpm).TpmPresent -and (Get-Tpm).TpmReady", "TPM is present and ready.",
            "TPM is absent or not ready.", "Initialize TPM through an approved firmware and recovery process.",
            cancellationToken);
    }

    private async Task AddBooleanPowerShellCheck(List<ManagementPlaneCheck> checks, bool required,
        string id, string expression, string success, string failure, string remediation,
        CancellationToken cancellationToken)
    {
        if (!required)
        {
            checks.Add(Check(id, ManagementCheckStatus.NotApplicable, "Check is not required by the active policy."));
            return;
        }
        var result = await _probe.RunAsync("powershell.exe",
            "-NoProfile -NonInteractive -Command \"if(" + expression + "){exit 0}else{exit 3}\"",
            TimeSpan.FromSeconds(30), cancellationToken);
        checks.Add(Check(id, result.ExitCode == 0 ? ManagementCheckStatus.Healthy : ManagementCheckStatus.Critical,
            result.ExitCode == 0 ? success : failure, result.ExitCode == 0 ? null : result.Error, remediation));
    }

    internal static HostDirectoryKind Classify(bool domainJoined, bool azureAdJoined) =>
        (domainJoined, azureAdJoined) switch
        {
            (true, true) => HostDirectoryKind.Hybrid,
            (true, false) => HostDirectoryKind.ActiveDirectory,
            (false, true) => HostDirectoryKind.MicrosoftEntra,
            _ => HostDirectoryKind.Workgroup
        };

    private static bool ParseYes(string output, string key) =>
        string.Equals(ParseValue(output, key), "YES", StringComparison.OrdinalIgnoreCase);

    private static string? ParseValue(string output, string key)
    {
        var match = Regex.Match(output ?? string.Empty,
            @"(?im)^\s*" + Regex.Escape(key) + @"\s*:\s*(?<value>.*?)\s*$");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static ManagementPlaneCheck Check(string id, ManagementCheckStatus status, string summary,
        string? problem = null, string? remediation = null, bool attempted = false,
        bool succeeded = false) =>
        new(id, status, summary, problem, remediation, attempted, succeeded);
}
