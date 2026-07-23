# SIRK-Agent

SIRK-Agent jest wykonawczym komponentem endpointowym SIRK Management Platform.

## Aktualny zakres testowy

```text
SIRK-Agent.exe
├── Windows Service
├── zabezpieczony Named Pipe
├── SIRK Protocol v1
├── walidacja TTL, requestId i nonce
├── replay protection
├── modularny command dispatcher
├── System module
└── Workspace foundation
    ├── enumeracja sesji lokalnych i RDS
    ├── izolacja Session 0
    ├── walidacja Workspace.CaptureFrame
    ├── SIRK-WorkspaceHost
    └── bezpieczny launcher procesu sesyjnego
```

Dostepne polecenia:

- `System.Ping`
- `System.GetStatus`
- `System.GetCapabilities`
- `Workspace.GetCapabilities`
- `Workspace.CaptureFrame` - kontrakt, walidacja i bezpieczne bledy; rzeczywisty provider obrazu jest jeszcze w przygotowaniu

## Najprostszy test

Pobierz artefakt GitHub Actions:

```text
SIRK-Agent-TestBundle-win-x64
```

Rozpakuj go na testowym komputerze z Windows, uruchom Windows PowerShell jako administrator i wykonaj:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
cd C:\Sciezka\Do\SIRK-Agent-TestBundle-win-x64
.\Scripts\Test-SIRKAgent.ps1 -BundlePath .
```

Skrypt:

- instaluje `SIRK-Agent` jako usluge,
- instaluje `SIRK-WorkspaceHost`,
- sprawdza status uslugi,
- wykonuje testy IPC,
- sprawdza raport capability,
- sprawdza izolacje Session 0,
- sprawdza enumeracje sesji RDS,
- zapisuje raport JSON w `%TEMP%`.

## Instalacja reczna

```powershell
.\Scripts\Install-SIRKAgent.ps1 `
    -SourceExe .\Agent\SIRK-Agent.exe `
    -WorkspaceHostSource .\WorkspaceHost\SIRK-WorkspaceHost.exe
```

Dla pakietu produkcyjnego przekaz rowniez oczekiwany SHA-256 agenta:

```powershell
$Hash = (Get-FileHash .\Agent\SIRK-Agent.exe -Algorithm SHA256).Hash
.\Scripts\Install-SIRKAgent.ps1 `
    -SourceExe .\Agent\SIRK-Agent.exe `
    -WorkspaceHostSource .\WorkspaceHost\SIRK-WorkspaceHost.exe `
    -ExpectedSha256 $Hash
```

Instalator:

- kopiuje pliki do `%ProgramFiles%\SIRK\Agent`,
- ogranicza ACL katalogu,
- tworzy usluge `SIRKAgent`,
- ustawia delayed automatic start,
- konfiguruje kontrolowane restarty po awarii,
- uruchamia usluge,
- zwraca status, hash i stan podpisu.

## Test reczny IPC

```powershell
.\Client\SIRK-Agent.Client.exe System.Ping
.\Client\SIRK-Agent.Client.exe System.GetStatus
.\Client\SIRK-Agent.Client.exe System.GetCapabilities
.\Client\SIRK-Agent.Client.exe Workspace.GetCapabilities
```

Klient generuje nowe `requestId`, TTL i kryptograficzny `nonce` dla kazdego wywolania.

## Odinstalowanie

```powershell
.\Scripts\Uninstall-SIRKAgent.ps1 -Confirm:$false
```

Pozostawienie plikow przy usunieciu uslugi:

```powershell
.\Scripts\Uninstall-SIRKAgent.ps1 -KeepFiles -Confirm:$false
```

## Security baseline

- brak dynamicznego PowerShell,
- brak `Invoke-Expression` i `DownloadString`,
- brak pobierania runtime podczas zwyklej pracy,
- scisla allowlista `messageType`,
- ograniczenia rozmiaru i timeouty,
- TTL, UUID, nonce i replay protection,
- osobny proces dla operacji w sesji uzytkownika,
- izolacja Windows Session 0,
- jawne ACL lokalnego IPC,
- logi bez sekretow i danych obrazu,
- przygotowanie do podpisywania Authenticode.

## Granica obecnej wersji testowej

Ta wersja pozwala przetestowac instalacje, usluge, IPC, protokol, adapter MeshCentral, enumeracje sesji oraz fundament WorkspaceHost. Nie przesyla jeszcze rzeczywistego obrazu pulpitu. Provider obrazu i jednorazowy handshake Agent -> WorkspaceHost sa kolejnym etapem przed testem zdalnego pulpitu.
