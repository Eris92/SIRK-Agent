# Terminal Workspace

## Cel

Dodac interaktywny terminal dzialajacy bezposrednio w oknie przegladarki, powiazany z konkretnym hostem i sesja Workspace.

## Tryby

- `User` - PowerShell lub CMD uruchomione w kontekscie zalogowanego uzytkownika i jego sesji Windows.
- `SYSTEM` - PowerShell lub CMD uruchomione jako LocalSystem, dostepne tylko dla operatora z osobnym uprawnieniem administracyjnym.

## Architektura

```text
Browser terminal (xterm.js)
    -> WebSocket / relay pluginu
    -> MeshAgent transport
    -> WorkspaceTerminalHost.exe
    -> ConPTY
    -> powershell.exe / pwsh.exe / cmd.exe
```

Terminal ma korzystac z Windows ConPTY, a nie z cyklicznego uruchamiania `runcommands`. Zapewni to interaktywnosc, resize, Ctrl+C, kolory ANSI i dlugotrwale procesy.

## Bezpieczenstwo

- osobne uprawnienia `TerminalUser` i `TerminalSystem`,
- wyrazne oznaczenie kontekstu w UI,
- wymagane ponowne potwierdzenie przed startem terminala SYSTEM,
- jedna sesja terminala przypisana do konkretnego operatora i hosta,
- timeout bezczynnosci i natychmiastowe zamkniecie po utracie polaczenia,
- audyt startu, zakonczenia, hosta, operatora i kontekstu,
- bez zapisywania tresci polecen oraz sekretow w standardowym logu,
- limity bufora i przepustowosci.

## Etap realizacji

Terminal jest zaplanowany po ustabilizowaniu pojedynczej klatki DXGI, a przed pelnym streamingiem obrazu i obsluga inputu. Moze byc rozwijany niezaleznie od Virtual Display.

Kolejnosc:

1. prototyp ConPTY lokalnie,
2. `WorkspaceTerminalHost.exe`,
3. transport przez MeshAgent,
4. terminal User w przegladarce,
5. terminal SYSTEM z osobnym uprawnieniem,
6. resize, reconnect, timeout i audyt,
7. integracja z kartami Workspace A/B.
