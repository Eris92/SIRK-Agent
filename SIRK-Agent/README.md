# SIRK-Agent

Ten katalog zawiera fundament niezaleznego agenta SIRK Management Platform.

## Aktualny zakres

```text
SIRK-Agent.exe
├── Windows Service
├── IPC.NamedPipe
├── Command Dispatcher
├── Protocol Validator
├── Replay Protection
├── Diagnostics
└── przyszly Module Host
```

Dostepne polecenia:

- `System.Ping`
- `System.GetStatus`
- `System.GetCapabilities`

`Workspace.CaptureFrame` pozostaje kolejnym etapem i nie jest jeszcze wlaczone.

## Budowanie

```powershell
dotnet restore .\src\SIRK.Agent\SIRK.Agent.csproj
dotnet build .\src\SIRK.Agent\SIRK.Agent.csproj -c Release
dotnet publish .\src\SIRK.Agent\SIRK.Agent.csproj -c Release -o .\artifacts\SIRK-Agent
```

## Instalacja uslugi

Uruchom PowerShell jako administrator:

```powershell
.\scripts\Install-SIRKAgent.ps1 `
    -SourceExe .\artifacts\SIRK-Agent\SIRK-Agent.exe
```

Dla pakietu produkcyjnego nalezy przekazac oczekiwany hash:

```powershell
$Hash = (Get-FileHash .\artifacts\SIRK-Agent\SIRK-Agent.exe -Algorithm SHA256).Hash
.\scripts\Install-SIRKAgent.ps1 `
    -SourceExe .\artifacts\SIRK-Agent\SIRK-Agent.exe `
    -ExpectedSha256 $Hash
```

Instalator:

- kopiuje binarium do `%ProgramFiles%\SIRK\Agent`,
- ogranicza ACL katalogu,
- tworzy usluge `SIRKAgent`,
- ustawia delayed automatic start,
- konfiguruje kontrolowane restarty po awarii,
- uruchamia usluge i zwraca status, hash oraz stan podpisu.

## Test Named Pipe

```powershell
dotnet run --project .\tools\SIRK.Agent.Client\SIRK.Agent.Client.csproj -- System.Ping

dotnet run --project .\tools\SIRK.Agent.Client\SIRK.Agent.Client.csproj -- System.GetStatus

dotnet run --project .\tools\SIRK.Agent.Client\SIRK.Agent.Client.csproj -- System.GetCapabilities
```

Klient generuje nowe `requestId`, TTL oraz kryptograficzny `nonce` dla kazdego wywolania.

## Odinstalowanie

```powershell
.\scripts\Uninstall-SIRKAgent.ps1 -Confirm:$false
```

Aby usunac usluge, ale pozostawic pliki:

```powershell
.\scripts\Uninstall-SIRKAgent.ps1 -KeepFiles -Confirm:$false
```

## Wymagania bezpieczenstwa

- brak dynamicznego wykonywania kodu,
- brak pobierania runtime podczas zwyklej operacji,
- scisla walidacja schematu wiadomosci,
- limity rozmiaru, czasu i kolejek,
- ochrona przed ponownym uzyciem `nonce`,
- osobne procesy dla operacji w sesji uzytkownika,
- logi bez sekretow i danych obrazu,
- podpisywanie produkcyjnych binariow Authenticode,
- docelowy ACL Named Pipe dla SYSTEM i jawnie autoryzowanego adaptera.

## Kolejnosc dalszych prac

1. test integracyjny uruchamiajacy agenta i klienta,
2. jawny ACL Named Pipe dla uslugi i SIRK-MeshAdapter,
3. heartbeat i lokalny health state,
4. instalacja oraz aktualizacja przez MeshAgent,
5. `Workspace.CaptureFrame` przez proces sesyjny,
6. transport standalone do SIRK-Server.
