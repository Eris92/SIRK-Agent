from pathlib import Path

root = Path(__file__).resolve().parents[2]


def read(relative: str) -> str:
    return (root / relative).read_text(encoding='utf-8-sig')


def write(relative: str, value: str) -> None:
    (root / relative).write_text(value, encoding='utf-8', newline='\n')


def replace_once(value: str, old: str, new: str, label: str) -> str:
    if value.count(old) != 1:
        raise RuntimeError(f'{label}: {value.count(old)}')
    return value.replace(old, new, 1)

project_path = 'src/SirkAgent.Session/SirkAgent.Session.csproj'
project = read(project_path)
project = project.replace('    <UseWindowsForms>true</UseWindowsForms>\n', '')
project = project.replace('    <UseWPF>true</UseWPF>\n', '')
project = replace_once(
    project,
    '    <ImplicitUsings>enable</ImplicitUsings>\n',
    '    <UseSystemDrawing>true</UseSystemDrawing>\n    <ImplicitUsings>enable</ImplicitUsings>\n',
    'UseSystemDrawing')
project = replace_once(
    project,
    '  <ItemGroup>\n    <PackageReference Include="Vortice.Direct3D11"',
    '  <ItemGroup>\n    <PackageReference Include="System.Drawing.Common" Version="10.0.0" />\n    <PackageReference Include="Vortice.Direct3D11"',
    'System.Drawing.Common')
if 'UseWindowsForms' in project or 'UseWPF' in project:
    raise RuntimeError('Desktop SDK properties remain')
write(project_path, project)

workflow_path = '.github/workflows/dotnet10-contract.yml'
workflow = read(workflow_path)
workflow = replace_once(
    workflow,
    "          if ($legacy) {\n            $legacy | ForEach-Object { Write-Error $_ }\n            throw 'Legacy runtime or compatibility reference detected.'\n          }\n",
    "          if ($legacy) {\n            $legacy | ForEach-Object { Write-Error $_ }\n            throw 'Legacy runtime or compatibility reference detected.'\n          }\n\n          $sessionProject = Get-Content 'src/SirkAgent.Session/SirkAgent.Session.csproj' -Raw\n          if ($sessionProject -match '<UseWindowsForms>|<UseWPF>|Microsoft.WindowsDesktop.App') {\n            throw 'SirkAgent.Session still requires Windows Desktop Runtime.'\n          }\n          $sessionSource = (Get-ChildItem 'src/SirkAgent.Session' -File -Filter '*.cs' |\n            ForEach-Object { Get-Content $_.FullName -Raw }) -join \"`n\"\n          if ($sessionSource -match 'System.Windows.Forms|System.Windows.Automation|AutomationElement|SendKeys\\.') {\n            throw 'SirkAgent.Session still contains WinForms or WPF APIs.'\n          }\n",
    'source runtime contract')
workflow = replace_once(
    workflow,
    "          & pwsh -NoProfile -File tests/shared-updater-installer-contract.ps1\n",
    "          $publish = Join-Path $env:RUNNER_TEMP 'sirk-session-runtime-contract'\n          dotnet publish src/SirkAgent.Session/SirkAgent.Session.csproj -c Release -r win-x64 `\n            --self-contained false --no-restore -o $publish\n          if ($LASTEXITCODE -ne 0) { throw 'SirkAgent.Session publish failed.' }\n          $runtimeConfig = Get-Content (Join-Path $publish 'SirkAgent.Session.runtimeconfig.json') -Raw\n          if ($runtimeConfig -match 'Microsoft.WindowsDesktop.App') {\n            throw 'Published Session broker still requires Microsoft.WindowsDesktop.App.'\n          }\n          if ($runtimeConfig -notmatch 'Microsoft.NETCore.App') {\n            throw 'Published Session broker does not declare Microsoft.NETCore.App.'\n          }\n          $deps = Get-Content (Join-Path $publish 'SirkAgent.Session.deps.json') -Raw\n          if ($deps -match 'System.Windows.Forms|PresentationFramework|PresentationCore|WindowsBase') {\n            throw 'Published Session broker contains WinForms or WPF dependencies.'\n          }\n\n          & pwsh -NoProfile -File tests/shared-updater-installer-contract.ps1\n",
    'published runtime contract')
write(workflow_path, workflow)

package_path = 'tools/package/Build-SirkAgentFinalPackage.ps1'
package = read(package_path)
package = replace_once(
    package,
    "    requiredRuntime = 'Microsoft.NETCore.App 10.0'\n    compatibilityMode = $false\n",
    "    requiredRuntime = 'Microsoft.NETCore.App 10.0'\n    desktopRuntimeRequired = $false\n    compatibilityMode = $false\n",
    'runtime manifest')
package = replace_once(
    package,
    "$forbidden = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')\n",
    "$sessionRuntimeConfig = Get-Content (Join-Path $package 'Session\\SirkAgent.Session.runtimeconfig.json') -Raw\nif ($sessionRuntimeConfig -match 'Microsoft.WindowsDesktop.App') {\n    throw 'Session broker still requires Microsoft.WindowsDesktop.App.'\n}\n\n$forbidden = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')\n",
    'package runtime rejection')
write(package_path, package)

print('Session project and package contracts no longer allow Windows Desktop Runtime.')
