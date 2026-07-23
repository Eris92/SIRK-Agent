# Architektura SIRK Management Platform

## Granice systemu

```text
SIRK-Portal / API / SDK / MeshCentral
                ↓
        Warstwa adapterow
                ↓
          SIRK-Server
                ↓
         SIRK-Protocol
                ↓
           SIRK-Agent
                ↓
 Workspace | Terminal | Files | Automation | Integrations
```

## Zasada niezaleznosci

Modul wykonawczy nie moze wiedziec, czy zadanie przyszlo z MeshCentral, SIRK-Portal, CLI czy SDK. Otrzymuje zweryfikowane polecenie wewnetrzne i zwraca ustandaryzowany wynik.

## SIRK-Agent

Agent jest usluga Windows dzialajaca jako SYSTEM, ale operacje w kontekscie uzytkownika deleguje do kontrolowanego procesu sesyjnego.

Planowane elementy:

```text
SIRK-Agent.exe
├── Core
├── Policy Engine
├── Command Dispatcher
├── Transport.Mesh
├── Transport.Standalone
├── IPC.NamedPipe
├── Update Client
├── Watchdog
├── Diagnostics
└── Module Host
```

Komponenty sesyjne i multimedialne nie powinny dzialac stale jako SYSTEM bez potrzeby.

## Model przejsciowy

MeshAgent instaluje, aktualizuje i uruchamia SIRK-Agent. MeshCentral pozostaje kanalem awaryjnym do czasu uruchomienia SIRK-Server.

```text
MeshAgent → lokalne IPC → SIRK-Agent → modul wykonawczy
```

Nie wolno rozwijac normalnej komunikacji runtime przez generowane one-linery PowerShell.

## Docelowe repozytoria

- SIRK-Agent
- SIRK-Server
- SIRK-Portal
- SIRK-Protocol
- SIRK-SDK
- SIRK-MeshAdapter
- SIRK-Installer
- SIRK-Docs

Rozdzielenie repozytoriow nastapi po ustabilizowaniu kontraktow pomiedzy komponentami.
