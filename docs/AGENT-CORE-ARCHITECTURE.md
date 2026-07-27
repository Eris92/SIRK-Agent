# SIRK Agent Core Architecture

## Cel

Docelowy produkt testowy ma byc pelnym, instalowalnym agentem Windows przeznaczonym do testow na wydzielonym sprzecie. Agent nie moze wymagac lokalnej edycji konfiguracji i nie moze pakowac calego .NET Runtime do kazdego pliku EXE.

## Moduly

- Bootstrap i Device Identity
- Security State Machine
- Policy Engine
- Tamper Protection i Quarantine
- Scheduler
- Health Monitor
- Watchdog
- Heartbeat
- Telemetry Queue
- Evidence Engine
- Recovery Engine
- Update Engine
- IPC
- Capture
- Browser Connector
- Command Executor
- HTML Diagnostics Report

## Security State Machine

Stany docelowe:

1. Boot
2. Operational
3. Degraded
4. PolicyExpired
5. TamperDetected
6. Quarantine
7. RecoveryPending
8. Recovering
9. RecoveryFailed
10. Stopping

Wyjscie z kwarantanny jest mozliwe tylko po zweryfikowaniu podpisanej polityki odzyskiwania dostarczonej z serwera.

## Wersja testowa

Gotowy pakiet testowy powinien zawierac:

- runtime agenta w wersji framework-dependent,
- instalacje i usuniecie uslugi Windows,
- test jednorazowy i tryb ciagly,
- chroniony stan polityki i kwarantanny,
- FileSystemWatcher,
- heartbeat i lokalna kolejke zdarzen,
- raport HTML,
- skrypt zebrania paczki diagnostycznej,
- jednoznaczne numery wersji i build commit.

## Raport HTML

Raport jest wymaganym elementem produktu testowego. Musi pokazywac stan wszystkich modulow jako Healthy, Warning albo Critical. Kazdy modul ma miec rozwijana sekcje z:

- podsumowaniem,
- sciezkami plikow,
- aktualnym stanem,
- ostatnimi zdarzeniami,
- pelna trescia wyjatku lub bledu,
- czasem ostatniej aktualizacji.

Raport nie moze wymagac serwera ani dostepu do Internetu.