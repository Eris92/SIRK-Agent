# MeshCentral Workspace

## 0.6.0

- Dodano trzy sloty: `User`, `Admin 1` i `Admin 2`.
- Kazdy slot ma osobny Session ID, wlasciciela, proces bootstrap i worker.
- Dodano blokade zajetego slotu przez innego administratora.
- WorkspaceHost uzywa osobnego Named Pipe dla kazdego slotu.
- Interfejs pokazuje trzy niezalezne karty i pozwala je uruchamiac oraz zatrzymywac.
- Admin 1 i Admin 2 sa na tym etapie logicznie oddzielnymi workerami na pulpicie `default`; ukryte desktopy beda kolejnym etapem.
- WorkspaceHost podniesiono do wersji 0.5.0.

## 0.5.0

- MeshAgent uruchamia bootstrap WorkspaceHost jako SYSTEM.
- Bootstrap wyszukuje aktywna sesje konsolowa lub RDP z zalogowanym uzytkownikiem.
- Token uzytkownika jest pobierany przez `WTSQueryUserToken` i duplikowany jako token podstawowy.
- Worker jest uruchamiany na `winsta0\\default` przez `CreateProcessAsUser`, z awaryjnym `CreateProcessWithTokenW`.
- Plugin czeka na heartbeat workera zamiast wymagac, aby PID heartbeat byl PID-em bootstrapu.
- Widok pokazuje osobno Bootstrap PID i Worker PID.
- Pliki runtime sa zapisywane w `C:\ProgramData\SirK\Workspace`.
- WorkspaceHost podniesiono do wersji 0.4.0.

## 0.4.2

- WorkspaceHost jest uruchamiany przez MeshAgent w trybie interaktywnego uzytkownika zamiast jako SYSTEM.
- Heartbeat raportuje liczbe monitorow, rozdzielczosc ekranu glownego i rozmiar pulpitu wirtualnego.
- Dodano prawidlowe kodowanie JSON dla nazw uzytkownikow domenowych.
- Widok Pulpit -New pokazuje dane przygotowujace kolejny etap DXGI Desktop Duplication.
- WorkspaceHost podniesiono do wersji 0.3.0.

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
