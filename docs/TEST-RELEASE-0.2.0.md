# SIRK Agent 0.2.0-test

## Cel wydania

Pierwsza instalowalna wersja testowa Windows x64, framework-dependent, przeznaczona do uruchomienia na wydzielonym komputerze testowym.

## Wymagania

- Windows 10/11 lub Windows Server x64,
- Microsoft .NET 8 Runtime x64,
- lokalne uprawnienia administratora do instalacji uslugi,
- testowy komputer, nie stacja produkcyjna.

## Zawartosc pakietu

- `SirkAgent.Service.exe` - runtime oraz usluga Windows,
- `SirkAgent.Report.exe` - raport HTML i JSON,
- `Install-SirkAgent.ps1` - instalacja uslugi,
- `Uninstall-SirkAgent.ps1` - usuniecie uslugi,
- `Collect-SirkAgent-TestBundle.ps1` - eksport jednej paczki diagnostycznej ZIP,
- launchery CMD do najwazniejszych operacji,
- `build-manifest.json` z wersja i SHA commita.

## Zaimplementowane moduly

- Device Identity zabezpieczony DPAPI LocalMachine,
- Policy Engine i chroniony policy state,
- Security State Machine,
- Scheduler: Startup, Interval oraz FileSystemWatcher,
- Health Registry i Health Monitor,
- Tamper Protection,
- trwala fail-closed Quarantine,
- heartbeat oraz lokalny event log,
- chroniona offline Telemetry Queue z limitem 50 MB,
- Evidence Chain z monotoniczna sekwencja, SHA-256 i chronionym stanem,
- recovery procesu uslugi skonfigurowane przez SCM,
- raport diagnostyczny HTML i JSON,
- TestBundle ZIP.

## Pierwszy test

1. Rozpakuj ZIP do lokalnego katalogu.
2. Uruchom `TEST-AGENT-ONCE.cmd`.
3. Uruchom `GENERATE-HTML-AND-JSON-REPORT.cmd`.
4. Sprawdz katalog `C:\ProgramData\SIRK\Agent`.
5. Uruchom jako administrator `INSTALL-SERVICE-AS-ADMIN.cmd`.
6. Sprawdz `Get-Service SirkAgent`.
7. Zrestartuj komputer i potwierdz ponowne uruchomienie uslugi.
8. Uruchom `COLLECT-TEST-BUNDLE.cmd` i przeslij utworzony ZIP do analizy.

## Oczekiwany stan bez polityki

Brak poprawnego, chronionego stanu polityki jest traktowany fail-closed. Agent moze pokazac stan `Critical` i aktywowac kwarantanne. Nie oznacza to awarii programu; jest to oczekiwane zachowanie ochronne.

## Dane lokalne

```text
C:\ProgramData\SIRK\Agent\device-identity.bin
C:\ProgramData\SIRK\Agent\policy-state.bin
C:\ProgramData\SIRK\Agent\quarantine-state.bin
C:\ProgramData\SIRK\Agent\quarantine-status.json
C:\ProgramData\SIRK\Agent\security-state.json
C:\ProgramData\SIRK\Agent\heartbeat-latest.json
C:\ProgramData\SIRK\Agent\agent-events.jsonl
C:\ProgramData\SIRK\Agent\TelemetryQueue\
C:\ProgramData\SIRK\Agent\evidence-events.jsonl
C:\ProgramData\SIRK\Agent\evidence-state.bin
C:\ProgramData\SIRK\Agent\Reports\
```

## Ograniczenia wersji 0.2.0-test

- brak polaczenia z produkcyjnym serwerem SIRK,
- brak automatycznego wysylania telemetry queue,
- brak podpisanej Recovery Policy do wyjscia z kwarantanny,
- brak produkcyjnego Update Engine,
- brak produkcyjnego IPC, Capture, Browser Connector i Command Executor,
- przeznaczenie: walidacja Agent Core, trwalosci, diagnostyki i zachowania fail-closed.

Te ograniczenia musza pozostac widoczne. Wersja 0.2.0-test nie jest wydaniem produkcyjnym.
