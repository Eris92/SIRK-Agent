using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text.Json;

static string Require(IReadOnlyDictionary<string, string> values, string name)
{
    if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"Missing required argument --{name}.");
    return value.Trim();
}

static Dictionary<string, string> Parse(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
        var name = args[index][2..];
        var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++index]
            : "true";
        result[name] = value;
    }
    return result;
}

static int Run(string file, IEnumerable<string> arguments, string? workingDirectory = null, bool requireSuccess = true)
{
    var info = new ProcessStartInfo(file)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
    };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {file}.");
    process.WaitForExit();
    if (requireSuccess && process.ExitCode != 0)
        throw new InvalidOperationException($"{file} failed with ExitCode={process.ExitCode}.");
    return process.ExitCode;
}

static string Capture(string file, IEnumerable<string> arguments)
{
    var info = new ProcessStartInfo(file)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {file}.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{file} failed with ExitCode={process.ExitCode}: {error}");
    return output;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static string DotNetPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

static bool HasDotNet10Runtime()
{
    var dotnet = DotNetPath();
    if (!File.Exists(dotnet)) return false;
    try
    {
        return Capture(dotnet, new[] { "--list-runtimes" })
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.StartsWith("Microsoft.NETCore.App 10.", StringComparison.Ordinal));
    }
    catch { return false; }
}

static void EnsureDotNet10Runtime()
{
    if (HasDotNet10Runtime()) return;
    Console.WriteLine("[PREREQUISITE] Installing Microsoft .NET 10 Runtime...");

    var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "winget.exe");
    if (!File.Exists(winget))
    {
        var bootstrap = "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; " +
            "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; " +
            "if (-not (Get-PackageProvider NuGet -ListAvailable -ErrorAction SilentlyContinue)) " +
            "{ Install-PackageProvider NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null }; " +
            "Set-PSRepository PSGallery -InstallationPolicy Trusted; " +
            "Install-Module Microsoft.WinGet.Client -Scope AllUsers -Force -AllowClobber; " +
            "Import-Module Microsoft.WinGet.Client -Force; Repair-WinGetPackageManager -AllUsers";
        Run("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", bootstrap });
    }

    if (!File.Exists(winget))
    {
        var package = Capture("powershell.exe", new[]
        {
            "-NoLogo", "-NoProfile", "-Command",
            "(Get-AppxPackage -AllUsers Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1).InstallLocation"
        }).Trim();
        var candidate = Path.Combine(package, "winget.exe");
        if (File.Exists(candidate)) winget = candidate;
    }
    if (!File.Exists(winget)) throw new InvalidOperationException("WinGet bootstrap failed.");

    Run(winget, new[]
    {
        "install", "--id", "Microsoft.DotNet.Runtime.10", "--exact", "--silent",
        "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
    });
    if (!HasDotNet10Runtime()) throw new InvalidOperationException("Microsoft .NET 10 Runtime is unavailable after installation.");
    Console.WriteLine("[OK] Microsoft .NET 10 Runtime installed.");
}

static string ResolveRuntimeAsset(JsonElement releases)
{
    foreach (var release in releases.EnumerateArray())
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains("win-x64-framework-dependent", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return asset.GetProperty("browser_download_url").GetString() ?? "";
        }
    }
    return "";
}

if (!OperatingSystem.IsWindows()) return 2;
try
{
    if (!IsAdministrator()) throw new InvalidOperationException("Run SIRK Agent Setup as Administrator.");
    EnsureDotNet10Runtime();

    var values = Parse(args);
    var portalOrigin = Require(values, "portal-url").TrimEnd('/');
    if (!Uri.TryCreate(portalOrigin, UriKind.Absolute, out var portalUri) || portalUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("--portal-url must be an HTTPS origin.");
    var token = Require(values, "enrollment-token");
    if (token.Length < 20 || token.Length > 256) throw new InvalidOperationException("Enrollment token is invalid.");
    var channel = values.GetValueOrDefault("channel", "stable");
    if (channel is not ("stable" or "dev")) throw new InvalidOperationException("--channel must be stable or dev.");

    var work = Path.Combine(Path.GetTempPath(), "SIRK-Agent-Setup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SIRK-Agent-Setup", "1.0"));
        using var releaseResponse = await client.GetAsync("https://api.github.com/repos/Eris92/SIRK-Agent/releases?per_page=20");
        releaseResponse.EnsureSuccessStatusCode();
        using var releases = JsonDocument.Parse(await releaseResponse.Content.ReadAsStreamAsync());
        var assetUrl = ResolveRuntimeAsset(releases.RootElement);
        if (string.IsNullOrWhiteSpace(assetUrl))
            throw new InvalidOperationException("No recent Agent release contains the Windows x64 runtime package.");

        var zip = Path.Combine(work, "agent.zip");
        await using (var output = File.Create(zip))
        await using (var input = await client.GetStreamAsync(assetUrl))
            await input.CopyToAsync(output);
        ZipFile.ExtractToDirectory(zip, work, true);

        var installer = Directory.EnumerateFiles(work, "Install-SirkAgent-WithUpdater.ps1", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("Canonical Agent installer is missing from the release package.");
        Run("powershell.exe", new[]
        {
            "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", installer, "-Channel", channel
        }, Path.GetDirectoryName(installer));

        var cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SIRK", "Agent", "sirkctl.exe");
        if (!File.Exists(cli)) throw new FileNotFoundException("Installed sirkctl.exe was not found.", cli);
        var tokenFile = Path.Combine(work, "enrollment-token.txt");
        await File.WriteAllTextAsync(tokenFile, token);
        Run(cli, new[] { "enroll", "--endpoint", portalOrigin + "/api/agent/v1/enroll", "--bootstrap-token-file", tokenFile });
        Run(cli, new[] { "sync" });

        var credential = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SIRK", "Agent", "portal-credential.bin");
        if (!File.Exists(credential))
            throw new InvalidOperationException("Agent enrollment completed without portal-credential.bin.");
        foreach (var service in new[] { "SirkAgent", "SirkAgentWatchdog", "SirkUpdater" })
            Run("sc.exe", new[] { "query", service });
        Console.WriteLine("SIRK_AGENT_SETUP_OK");
        return 0;
    }
    finally { try { Directory.Delete(work, true); } catch { } }
}
catch (Exception error)
{
    Console.Error.WriteLine("SIRK_AGENT_SETUP_FAILED: " + error.Message);
    return 1;
}
