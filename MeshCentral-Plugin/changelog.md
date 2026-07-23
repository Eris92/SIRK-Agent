# MeshCentral Workspace

## 0.8.2

- Usunieto delegacje klikniec z kontenera pluginu.
- Przyciski `Przygotuj`, `Utworz`, `Zatrzymaj` i `Odswiez` dostaja bezposredni `onclick` po kazdym renderowaniu kart.
- Loader zeruje stary `bootstrapPromise` i wymusza ponowne zaladowanie `main.js` po aktualizacji.
- Stan `wysylanie` nadal pojawia sie natychmiast po kliknieciu.

## 0.8.1

- Dodano `type="button"` do wszystkich przyciskow, aby formularz MeshCentral nie przejmowal klikniecia.
- Obsluga klikniec korzysta teraz z delegacji zdarzen na kontenerze pluginu.
- Po kliknieciu `Przygotuj` lub `Utworz` stan natychmiast zmienia sie na `wysylanie`.
- Bledy wywolania sa pokazywane bezposrednio w karcie zamiast pozostawiac wrazenie, ze przycisk nic nie robi.
- Wymuszono ponowne zaladowanie pliku `main.js` po aktualizacji pluginu.

## 0.7.0

- Sesja `User` pozostaje na pulpicie `winsta0\\default`.
- `Workspace A` tworzy ukryty desktop `winsta0\\SirK-Admin-1`.
- `Workspace B` tworzy ukryty desktop `winsta0\\SirK-Admin-2`.
- Worker administracyjny tworzy desktop przez `CreateDesktopW` i przechodzi na niego przez `SetThreadDesktop`.
- Ukryte desktopy nie sa przelaczane jako aktywne, dlatego uzytkownik nie widzi okien administratora.
- Interfejs rozroznia sesje uzytkownika od dwoch izolowanych workspace administracyjnych.
- WorkspaceHost podniesiono do wersji 0.6.0.

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