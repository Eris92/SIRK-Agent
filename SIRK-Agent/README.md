# SIRK-Agent

Ten katalog zawiera niezaleznego agenta endpointowego SIRK Management Platform.

## Aktualny zakres testowy

```text
SIRK-Agent.exe
├── Windows Service
├── zabezpieczony IPC Named Pipe
├── SIRK Protocol v1
├── Command Dispatcher
├── Replay Protection
├── System module
└── Workspace module
    ├── enumeracja sesji lokalnych i RDS
    ├── izolacja Session 0
    ├── launcher CreateProcessAsUser
    ├── jednorazowy token handshake
    └── Workspace.CaptureFrame
        └── GDI -> JPEG
```

Dostepne polecenia:

- `System.Ping`
- `System.GetStatus`
- `System.GetCapabilities`
- `Workspace.GetCapabilities`
- `Workspace.CaptureFrame`

Pierwszy provider obrazu obsluguje:

- aktywna sesje lokalna lub RDS,
- `monitorId: primary`,
- `monitorId: all`,
- JPEG quality 20-95,
- ograniczenie maksymalnej szerokosci i wysokosci,
- opcjonalny kursor,
- pojedyncza klatke obrazu.

To nie jest jeszcze strumien pulpitu ani sterowanie mysza/klawiatura. Ta paczka sluzy do sprawdzenia poprawnosci uruchamiania procesu sesyjnego, handshake oraz rzeczywistego przechwycenia JPEG.

## Test funkcjonalny

Uruchom Windows PowerShell jako administrator w katalogu rozpakowanej paczki:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Scripts\Test-SIRKAgent.ps1 -BundlePath .
```

Skrypt:

1. sprawdza .NET 8 Runtime,
2. instaluje lub aktualizuje usluge,
3. uruchamia testy IPC,
4. wybiera aktywna sesje Windows,
5. uruchamia `SIRK-WorkspaceHost.exe` w tej sesji,
6. weryfikuje jednorazowy handshake,
7. przechwytuje pulpit do JPEG,
8. zapisuje screenshot i raport JSON w `%TEMP%`.

Poprawny wynik:

```text
SIRK-Agent functional workspace test passed.
Screenshot: ...jpg
Report:     ...json
Capture:    ... ms / ... bytes
```

## Instalacja reczna

```powershell
.\scripts\Install-SIRKAgent.ps1 `
    -SourceExe .\Agent\SIRK-Agent.exe `
    -WorkspaceHostSource .\WorkspaceHost\SIRK-WorkspaceHost.exe
```

Instalator kopiuje cale katalogi publikacji framework-dependent do:

```text
%ProgramFiles%\SIRK\Agent
```

Uzywa stalych SID zamiast zlokalizowanych nazw kont, dlatego dziala na polskim i angielskim Windows.

## Wymagania

- Windows 10/11 albo Windows Server z interaktywna sesja uzytkownika,
- .NET 8 Runtime x64,
- uruchomienie instalatora jako administrator,
- usluga `SIRKAgent` dzialajaca jako LocalSystem.

## Bezpieczenstwo

- brak dynamicznego PowerShell i `Invoke-Expression`,
- brak pobierania kodu runtime,
- jawna allowlista `messageType`,
- UUID, TTL, nonce i replay protection,
- osobne limity zadania i odpowiedzi,
- Session 0 jest blokowana,
- WorkspaceHost sprawdza rzeczywisty numer swojej sesji,
- nazwa pipe jest losowa,
- token handshake ma 256 bitow i jest porownywany w stalym czasie,
- PID procesu oraz numer sesji sa ponownie sprawdzane po polaczeniu,
- dane obrazu nie sa zapisywane w logach agenta.

## Odinstalowanie

```powershell
.\scripts\Uninstall-SIRKAgent.ps1 -Confirm:$false
```

## Nastepny etap

- stabilny stream wielu klatek,
- benchmark FPS i opoznienia,
- wejscie myszy i klawiatury,
- pelna enumeracja monitorow,
- DXGI Desktop Duplication jako provider docelowy.