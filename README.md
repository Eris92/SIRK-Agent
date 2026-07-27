# SIRK Agent

Agent Windows rozwijany jako niezalezny komponent platformy SIRK. Projekt laczy bezpieczne egzekwowanie centralnych polityk, ochrone przed manipulacja, diagnostyke, telemetrie, funkcje dochodzeniowe oraz docelowo zdalne zarzadzanie i mechanizmy klasy EDR/XDR.

## Kontynuacja projektu

Najwazniejszym dokumentem przekazania do nowego czatu jest:

- [Kontynuacja projektu w nowym czacie](docs/CONTINUE-IN-NEW-CHAT.md)

Zawiera aktualny stan implementacji, zasady bezpieczenstwa, strukture repozytorium, testy wykonane na Windows, najblizsze zadania oraz gotowa wiadomosc startowa do nowego czatu.

Aktualny branch roboczy:

```text
policy-engine-foundation
```

Aktualny Pull Request:

```text
#2 - Policy Engine foundation and security roadmap
```

## Zasady architektoniczne

1. Agent nie ufa konfiguracji lokalnego hosta.
2. Polityki, aktualizacje i moduly musza byc podpisane przez zaufany system zarzadzania.
3. Brak telemetrii, utrata heartbeat albo niezgodnosc integralnosci sa zdarzeniami bezpieczenstwa.
4. Tryby rozszerzonego monitoringu musza miec Case ID, zakres, termin waznosci i audit.
5. Dane dowodowe musza miec spojny czas UTC, identyfikator urzadzenia, sesji, uzytkownika i lancuch integralnosci.
6. Nie dolaczamy calego .NET do EXE ani paczki. Build Windows x64 pozostaje framework-dependent dla .NET 8.
7. Wyjscie z kwarantanny moze nastapic tylko przez podpisana Recovery Policy z serwera.

## Aktualny stan implementacji

Dzialajace elementy:

- Policy Engine z podpisem ES256,
- Tenant ID, Device ID, epoch, version i nonce,
- ochrona przed replay i rollback,
- stan polityki chroniony DPAPI LocalMachine,
- atomowy zapis lokalnego stanu,
- heartbeat i lokalny log zdarzen,
- FileSystemWatcher dla natychmiastowej detekcji manipulacji,
- trwaly Quarantine Mode,
- chroniony DPAPI stan kwarantanny,
- fail-closed po uszkodzeniu stanu kwarantanny,
- raport diagnostyczny HTML z rozwijanymi szczegolami i bledami,
- eksport pelnej diagnostyki do JSON,
- testy jednostkowe i GitHub Actions dla Windows.

Potwierdzony test na komputerze Windows `DELL_K` wykryl reczna modyfikacje `policy-state.bin` jako `STATE_UNPROTECT_FAILED`, aktywowal kwarantanne i zachowal ja pomiedzy kolejnymi cyklami.

## Pliki runtime na komputerze testowym

```text
C:\ProgramData\SIRK\Agent\policy-state.bin
C:\ProgramData\SIRK\Agent\heartbeat-latest.json
C:\ProgramData\SIRK\Agent\agent-events.jsonl
C:\ProgramData\SIRK\Agent\tamper-event-latest.json
C:\ProgramData\SIRK\Agent\quarantine-state.bin
C:\ProgramData\SIRK\Agent\quarantine-status.json
C:\ProgramData\SIRK\Agent\Reports\
```

Generator diagnostyki tworzy:

```text
SIRK-Agent-Status-YYYYMMDD-HHMMSS.html
SIRK-Agent-Status-YYYYMMDD-HHMMSS.json
```

JSON jest przeznaczony do przeslania w kolejnym czacie w celu analizy wynikow testow.

## Docelowa struktura Agent Core

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

## Struktura repozytorium

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

## Roadmap

### Agent Core

- rozdzielenie `Program.cs` na moduly,
- trwaly Device Identity,
- Security State Machine,
- Scheduler,
- rejestr modulow,
- Health Monitor,
- Watchdog,
- instalacja jako Windows Service.

### Security Foundation

- podpisane paczki polityk,
- przypisanie polityki do Tenant ID i Device ID,
- ochrona przed replay i rollback,
- certyfikat urzadzenia i klucz nieeksportowalny,
- Tamper Detection i Quarantine,
- podpisana Recovery Policy,
- podpisane aktualizacje i moduly.

### Evidence and Activity

- Evidence Engine z lancuchem hashy,
- offline Telemetry Queue,
- aktywne procesy, okna i czas aktywnosci,
- metadane schowka,
- operacje na plikach, USB, drukowanie i archiwizacja,
- telemetria myszy i dynamiki klawiatury bez domyslnego zapisu tresci,
- screenshot na zdarzenie oraz ograniczony stream dochodzeniowy.

### Investigation and Insider Risk

- czasowy Investigation Mode,
- scenariusz Departing Employee / Insider Risk,
- korelacja pobrania, kopiowania, kompresji, uploadu, wysylki i usuniecia,
- integracja Edge/Chrome dla URL, upload i download,
- raport dowodowy z osia czasu,
- scoring ryzyka i wykrywanie anomalii operatora.

## Dokumentacja

- [Kontynuacja projektu w nowym czacie](docs/CONTINUE-IN-NEW-CHAT.md)
- [Agent Core Architecture](docs/AGENT-CORE-ARCHITECTURE.md)
- [Roadmap Security](docs/ROADMAP-SECURITY.md)
- [Policy Engine](docs/POLICY-ENGINE.md)
- [Investigation i Insider Risk](docs/INVESTIGATION-INSIDER-RISK.md)
- [Tamper Protection](docs/TAMPER-PROTECTION.md)
- [Evidence Engine](docs/EVIDENCE-ENGINE.md)

## Wiadomosc startowa do nowego czatu

```text
Kontynuuj projekt SIRK Agent z repozytorium Eris92/SIRK-Agent, branch policy-engine-foundation, PR #2. Przeczytaj docs/CONTINUE-IN-NEW-CHAT.md oraz pozostala dokumentacje. Kontynuuj autonomicznie od sekcji Najblizsze zadania do realizacji. Nie pakuj calego .NET do EXE. Po kazdym stabilnym etapie uruchom CI i przygotuj paczke Windows x64 do testow. Raport diagnostyczny musi generowac HTML i JSON ze stanem wszystkich modulow oraz rozwijanymi szczegolami bledow.
```
