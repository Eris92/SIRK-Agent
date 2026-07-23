# MeshCentral Workspace

Alternatywny modul zdalnego pulpitu dla MeshCentral rozwijany jako osobna wtyczka i host Windows.

## Wersja 0.1.0

Pierwsza wersja fundamentu zawiera:

- szkielet pluginu MeshCentral,
- zakladke urzadzenia `Pulpit -New`,
- serwerowy model sesji i endpointy start/status/heartbeat/stop,
- `WorkspaceHost.exe` dla .NET 8,
- heartbeat JSON przez Named Pipe,
- log do `C:\ProgramData\SirK\Workspace\Logs\workspace.log`,
- skrypt budowania plugin ZIP i hosta Windows,
- GitHub Actions dla branchy `main` i `develop`,
- dokumentacje architektury, budowania i roadmapy.

## Aktualny przeplyw

```text
Browser
  -> Pulpit -New
  -> Workspace session API
  -> server-side session state

WorkspaceHost.exe
  -> Named Pipe
  -> heartbeat JSON
```

Warstwa uruchamiania `WorkspaceHost.exe` i przekazywania heartbeat przez MeshAgent jest zaplanowana jako v0.2.0. Wersja 0.1.0 przygotowuje obie strony tego polaczenia, ale nie modyfikuje jeszcze MeshAgenta.

## Struktura

```text
MeshCentral-Plugin/   plugin i UI
WorkspaceHost/        proces Windows uruchamiany w sesji usera
WorkspaceCommon/      wspolne modele protokolu
docs/                 dokumentacja
tools/                build i narzedzia
.github/workflows/    CI
```

## Build

```powershell
.\tools\build.ps1 -Configuration Release -Runtime win-x64
```

Wyniki:

```text
artifacts\MeshCentral-Workspace-Plugin-0.1.0.zip
artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe
```

Szczegoly: `docs/BUILD.md`.

## Branche

- `main` - wersja stabilna,
- `develop` - biezacy rozwoj.

Oryginalny modul Desktop MeshCentral pozostaje bez zmian.
