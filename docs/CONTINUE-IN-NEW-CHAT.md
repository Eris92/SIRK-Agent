# Kontynuacja projektu SIRK Agent w nowym czacie

Ten dokument jest punktem przekazania projektu. W nowym czacie nalezy podac repozytorium, branch oraz ten plik i poprosic o kontynuowanie prac od aktualnego stanu.

## Identyfikacja projektu

- Repozytorium: `Eris92/SIRK-Agent`
- Branch roboczy: `policy-engine-foundation`
- Pull Request: `#2 - Policy Engine foundation and security roadmap`
- Platforma docelowa: Windows x64
- Runtime: .NET 8, build framework-dependent
- Zasada pakowania: nie dolaczac calego .NET do EXE ani ZIP

## Cel projektu

SIRK Agent ma byc niezaleznym agentem Windows dla platformy SIRK, laczacym funkcje zdalnego zarzadzania, bezpiecznego egzekwowania polityk, telemetrii, ochrony przed manipulacja oraz przyszlych funkcji EDR/XDR i dochodzeniowych.

Docelowy produkt testowy ma dzialac na osobnym komputerze testowym, raportowac stan wszystkich modulow i generowac diagnostyke HTML oraz JSON.

## Fundamentalne zasady bezpieczenstwa

1. Agent nie ufa konfiguracji lokalnego hosta.
2. Konfiguracja moze pochodzic tylko z centralnego systemu zarzadzania i musi byc podpisana.
3. Brak lokalnych przelacznikow w JSON, rejestrze lub parametrach pozwalajacych wylaczyc ochrone.
4. Polityki maja Tenant ID, Device ID, Policy ID, epoch, version, nonce, czas waznosci i podpis.
5. Replay i rollback musza byc blokowane.
6. Stan lokalny jest chroniony przez Windows DPAPI LocalMachine.
7. Manipulacja, brak heartbeat lub brak telemetrii sa zdarzeniami bezpieczenstwa.
8. Wyjscie z kwarantanny ma byc mozliwe tylko przez podpisana Recovery Policy z serwera.
9. Pakiety produkcyjne maja byc framework-dependent; krytyczne lekkie moduly moga byc pozniej przepisywane do natywnego C++.

## Aktualnie zaimplementowane elementy

### Policy Engine

- modele podpisanej polityki,
- tryby: Normal, Security, Investigation, InsiderRisk, Emergency,
- kanonizacja JSON,
- weryfikacja podpisu ES256,
- powiazanie polityki z Tenant ID i Device ID,
- kontrola czasu waznosci,
- blokowanie replay przez nonce,
- blokowanie rollbacku przez epoch i version,
- wymagany Case ID dla trybow dochodzeniowych,
- atomowa akceptacja i zapis polityki.

### Chroniony stan polityki

Plik:

```text
C:\ProgramData\SIRK\Agent\policy-state.bin
```

Funkcje:

- szyfrowanie DPAPI LocalMachine,
- atomowy zapis,
- przechowywanie epoch, version, nonce, Policy ID, Case ID i SHA-256 aktywnej polityki,
- wykrywanie braku, pustego pliku, uszkodzenia, blednego JSON i bledu DPAPI.

### Heartbeat i runtime

Pliki:

```text
C:\ProgramData\SIRK\Agent\heartbeat-latest.json
C:\ProgramData\SIRK\Agent\agent-events.jsonl
```

Runtime:

- pracuje w petli co 30 sekund,
- kontroluje stan polityki,
- zapisuje heartbeat atomowo,
- dopisuje zdarzenia lokalne,
- obsluguje tryb `--once`,
- raportuje Policy ID, version, hash, status i trigger.

### Tamper Watcher

- FileSystemWatcher dla `policy-state.bin`,
- natychmiastowa kontrola po Changed, Created, Deleted lub Renamed,
- debounce 350 ms,
- trigger: Startup, Interval lub FileSystemWatcher,
- pola `tamperDetected` i `tamperReason` w heartbeat.

Potwierdzony test na komputerze `DELL_K`:

- poprawny stan raportowal `OK`,
- reczna edycja `policy-state.bin` zostala wykryta jako `STATE_UNPROTECT_FAILED`,
- watcher zareagowal bez oczekiwania na kolejny interwal.

### Quarantine Mode

Pliki:

```text
C:\ProgramData\SIRK\Agent\quarantine-state.bin
C:\ProgramData\SIRK\Agent\quarantine-status.json
C:\ProgramData\SIRK\Agent\tamper-event-latest.json
```

Funkcje:

- trwala kwarantanna po wykryciu manipulacji,
- kwarantanna nie znika po przywroceniu poprawnego pliku polityki,
- chroniony stan kwarantanny przez DPAPI LocalMachine,
- czytelny podglad JSON dla administratora,
- zachowanie pierwszego czasu wykrycia,
- licznik kolejnych detekcji,
- zachowanie uszkodzonego pliku jako dowodu,
- fail-closed: uszkodzenie stanu kwarantanny ponownie wymusza kwarantanne,
- powod `QUARANTINE_STATE_TAMPER` dla manipulacji stanem kwarantanny.

### Raport diagnostyczny HTML i JSON

Projekt:

```text
src/SirkAgent.Report
```

Polecenia w paczce testowej:

```text
GENERATE-HTML-AND-JSON-REPORT.cmd
EXPORT-DIAGNOSTICS-JSON.cmd
```

Wyniki sa zapisywane w:

```text
C:\ProgramData\SIRK\Agent\Reports\
```

Generator tworzy jednoczesnie:

```text
SIRK-Agent-Status-YYYYMMDD-HHMMSS.html
SIRK-Agent-Status-YYYYMMDD-HHMMSS.json
```

Raport kontroluje obecnie:

- Policy / Heartbeat,
- Quarantine,
- Latest tamper event,
- ostatnie zdarzenia agenta,
- policy-state.bin,
- quarantine-state.bin,
- .NET Runtime,
- system, architekture procesu i wolne miejsce na dysku.

HTML:

- ogolny status: OK, OSTRZEZENIE lub KRYTYCZNY,
- rozwijane sekcje `<details>`,
- pelne dane diagnostyczne,
- pelne informacje o bledach i stack trace,
- ciemny responsywny interfejs.

JSON:

- nazwa urzadzenia,
- czas UTC i lokalny,
- ogolny stan,
- podsumowanie liczby Healthy, Warning i Critical,
- komplet danych kazdego modulu,
- szczegoly, sciezki i bledy,
- format przeznaczony do przeslania w kolejnym czacie do analizy.

## Aktualna struktura kodu

```text
src/
  SirkAgent.Policy/
  SirkAgent.Service/
  SirkAgent.Report/
tools/
  SirkAgent.Policy.TestHarness/
tests/
  SirkAgent.Policy.Tests/
docs/
schemas/
.github/workflows/policy-engine-ci.yml
```

## Docelowa architektura Agent Core

```text
SIRK Agent Service
|-- Bootstrap
|-- Device Identity
|-- Security State Machine
|-- Policy Engine
|-- Tamper Protection
|-- Quarantine
|-- Scheduler
|-- Health Monitor
|-- Watchdog
|-- Heartbeat Service
|-- Telemetry Queue
|-- Evidence Engine
|-- Recovery Engine
|-- Update Engine
|-- IPC Service
|-- Capture Service
|-- Browser Connector
|-- Command Executor
`-- Diagnostics Reporter
```

## Najblizsze zadania do realizacji

Kontynuowac autonomicznie, bez zatrzymywania sie po kazdym malym kroku, az powstanie kolejna stabilna paczka testowa.

Kolejnosc priorytetowa:

1. Rozdzielic rozbudowany `Program.cs` na moduly Agent Core.
2. Dodac trwaly Device Identity chroniony DPAPI.
3. Dodac centralna Security State Machine: Boot, Operational, Degraded, TamperDetected, Quarantine, RecoveryPending, Recovering, RecoveryFailed.
4. Dodac rejestr modulow i wspolny model Health.
5. Dodac Scheduler zamiast jednej petli sterujacej wszystkim.
6. Dodac Health Monitor z aktualnym stanem kazdego modulu.
7. Rozszerzyc raport HTML/JSON o wszystkie zarejestrowane moduly, uptime, wersje builda, czasy ostatnich sukcesow i pelne bledy.
8. Dodac offline Telemetry Queue z retry i limitem miejsca.
9. Dodac Evidence Queue z sekwencja i lancuchem hashy.
10. Dodac podpisana Recovery Policy jako jedyna droge wyjscia z kwarantanny.
11. Dodac instalacje jako Windows Service, uninstall, upgrade i pakiet testowy.
12. Dodac skrypt testow end-to-end oraz eksport jednej paczki diagnostycznej ZIP.

## Kryteria pierwszej pelnej wersji testowej

Wersja przekazana do testow na osobnym komputerze musi:

- instalowac i odinstalowywac usluge Windows,
- dzialac po restarcie systemu,
- korzystac z zainstalowanego .NET 8 bez dolaczania calego runtime,
- miec trwaly Device ID,
- uruchamiac Policy Engine, State Machine, Scheduler, Health Monitor, Watchdog, Heartbeat, Tamper Protection i Quarantine,
- zapisywac zdarzenia offline,
- generowac HTML oraz JSON ze stanem wszystkich modulow,
- pokazywac rozwijane pelne informacje o bledach,
- eksportowac paczke diagnostyczna do przeslania,
- miec testy manipulacji, restartu, braku plikow, uszkodzenia stanu i braku serwera,
- nie miec lokalnego sposobu wylaczenia zabezpieczen.

## Jak rozpoczac nowy czat

W nowym czacie uzyj wiadomosci:

```text
Kontynuuj projekt SIRK Agent z repozytorium Eris92/SIRK-Agent, branch policy-engine-foundation, PR #2. Przeczytaj docs/CONTINUE-IN-NEW-CHAT.md oraz pozostala dokumentacje. Kontynuuj autonomicznie od sekcji Najblizsze zadania do realizacji. Nie pakuj calego .NET do EXE. Po kazdym stabilnym etapie uruchom CI i przygotuj paczke Windows x64 do testow. Raport diagnostyczny musi generowac HTML i JSON ze stanem wszystkich modulow oraz rozwijanymi szczegolami bledow.
```

Do nowego czatu warto dolaczyc najnowszy plik:

```text
C:\ProgramData\SIRK\Agent\Reports\SIRK-Agent-Status-*.json
```

jesli testy byly wykonywane na komputerze.

## Dokumenty powiazane

- `docs/AGENT-CORE-ARCHITECTURE.md`
- `docs/POLICY-ENGINE.md`
- `docs/TAMPER-PROTECTION.md`
- `docs/EVIDENCE-ENGINE.md`
- `docs/INVESTIGATION-INSIDER-RISK.md`
- `docs/ROADMAP-SECURITY.md`

Ten dokument powinien byc aktualizowany przy kazdym wiekszym etapie lub wydaniu paczki testowej.
