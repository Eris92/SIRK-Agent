# Roadmap migracji

## Etap 0 - dokumentacja i kontrakty

- przyjecie nazwy SIRK Management Platform,
- zdefiniowanie granic komponentow,
- zdefiniowanie security baseline,
- fundament SIRK Protocol.

## Etap 1 - SIRK-Agent Foundation

- usluga Windows `SIRK-Agent`,
- instalacja przez MeshAgent,
- lokalne IPC przez Named Pipe,
- jawne ACL Named Pipe,
- heartbeat i capability report,
- command dispatcher,
- polityki lokalne,
- bezpieczny katalog runtime,
- wersjonowanie i diagnostyka.

## Etap 2 - migracja Workspace

Zrealizowane fundamenty:

- walidowany kontrakt `Workspace.CaptureFrame`,
- izolacja Windows Session 0,
- enumeracja sesji lokalnych i RDS przez WTS API,
- wybor aktywnej sesji interaktywnej,
- abstrakcja `IWorkspaceCaptureProvider`,
- przejscie MeshCentral -> SIRK-MeshAdapter -> SIRK-Agent.

Kolejne zadania:

- `WorkspaceHost` pod kontrola SIRK-Agent,
- bezpieczny proces pomocniczy uruchamiany w sesji uzytkownika,
- `captureFrame` bez dynamicznego PowerShell,
- pierwszy stabilny obraz DXGI,
- stale polaczenie capture,
- adaptacyjna kompresja dla bardzo slabych laczy,
- input, clipboard i virtual display.

## Etap 3 - modul Terminal

- ConPTY,
- tryb User i SYSTEM,
- osobne uprawnienia i audyt,
- limity czasu, rozmiaru wyjscia i procesow.

## Etap 4 - SIRK-Server i transport standalone

- enrollment urzadzen,
- mTLS i rotacja tozsamosci,
- portal i broker,
- relay dla sieci bez polaczen przychodzacych,
- rownolegle kanaly Mesh oraz standalone.

## Etap 5 - odpiecie MeshCentral

- SIRK-Server jako transport podstawowy,
- MeshCentral jako opcjonalny fallback,
- mozliwosc wylaczenia Mesh bez reinstalacji agenta.

## Etap 6 - integracje i kolejne platformy

- Linux i macOS,
- Hyper-V, VMware, Proxmox,
- Docker i Kubernetes,
- AD, Entra, Intune, Zabbix, Jira i inne adaptery,
- urzadzenia sieciowe, IoT i OT.

## Najblizsze zadanie implementacyjne

Zbudowac podpisany i ograniczony proces `SIRK-WorkspaceHost`, ktory jest uruchamiany przez usluge tylko w zweryfikowanej aktywnej sesji uzytkownika. Komunikacja Agent -> WorkspaceHost ma uzywac osobnego, jednorazowego kanalu IPC z limitem czasu, rozmiaru odpowiedzi i identyfikatorem zadania.