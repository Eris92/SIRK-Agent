# SIRK-Agent

Ten katalog jest punktem startowym niezaleznego agenta SIRK Management Platform.

## Pierwszy zakres implementacyjny

```text
SIRK-Agent.exe
├── Windows Service
├── IPC.NamedPipe
├── Command Dispatcher
├── Policy Engine
├── Heartbeat
├── Diagnostics
└── Workspace Adapter
```

## Pierwsze polecenia

- `System.GetStatus`
- `System.GetCapabilities`
- `System.Ping`
- `Workspace.CaptureFrame`

## Wymagania implementacyjne

- brak dynamicznego wykonywania kodu,
- brak pobierania runtime podczas zwyklej operacji,
- scisla walidacja schematu wiadomosci,
- limity rozmiaru, czasu i kolejek,
- ACL Named Pipe ograniczone do SYSTEM, administratorow i jawnie autoryzowanego procesu adaptera,
- osobne procesy dla operacji w sesji uzytkownika,
- anulowanie zadan i kontrolowany shutdown,
- logi bez sekretow i danych obrazu,
- testy jednostkowe parsera oraz polityk przed podlaczeniem funkcji uprzywilejowanych.

## Kolejnosc prac

1. wybrac docelowy runtime i model uslugi Windows,
2. utworzyc minimalna usluge z kontrolowanym stopem,
3. dodac Named Pipe i `System.Ping`,
4. dodac walidacje SIRK Protocol v1,
5. dodac heartbeat oraz capability report,
6. przygotowac instalacje przez MeshAgent,
7. przeniesc `Workspace.CaptureFrame`.
