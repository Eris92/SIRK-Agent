# MeshCentral Workspace

## 0.4.0

- Plugin po uruchomieniu `WorkspaceHost.exe` laczy sie z Named Pipe `SirK.MeshCentral.Workspace`.
- Stan `running` jest ustawiany dopiero po odebraniu i zweryfikowaniu prawdziwego heartbeat JSON.
- Widok pokazuje PID, Windows Session, uzytkownika, desktop, wersje oraz uptime.
- Bledy pobierania, SHA256, startu procesu i heartbeat sa zwracane do zakladki zamiast pozostawienia stanu `deploying`.
- Przycisk `Rozlacz` wysyla przez MeshAgent polecenie zatrzymania konkretnego PID WorkspaceHost.

## 0.3.3

- Dodano wymagany przez MeshCentral plik startowy `workspace.js`.

## 0.3.0

- Dodano uruchamianie WorkspaceHost przez MeshAgent.
- Dodano pobieranie EXE z release `develop-latest`.
- Dodano weryfikacje SHA256 przed uruchomieniem.
- Dodano statusy `requested`, `deploying`, `running` i `error` w zakladce Pulpit -New.
- Repozytorium udostepnia galaz `plugin` zawierajaca wylacznie gotowa wtyczke MeshCentral.
