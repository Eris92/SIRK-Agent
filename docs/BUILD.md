# Build

## Requirements

- Windows 10/11 or Windows Server
- PowerShell 7 or Windows PowerShell 5.1
- .NET 8 SDK

## Command

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\build.ps1 -Configuration Release -Runtime win-x64
```

Outputs:

```text
artifacts\WorkspaceHost-win-x64\
artifacts\MeshCentral-Workspace-Plugin-0.1.0.zip
```

For a self-contained host:

```powershell
.\tools\build.ps1 -Configuration Release -Runtime win-x64 -SelfContained
```

## Local host test

```powershell
.\artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe --pipe SirK.Workspace.Test --session local-test --heartbeat 2
```

The host writes logs to:

```text
C:\ProgramData\SirK\Workspace\Logs\workspace.log
```
