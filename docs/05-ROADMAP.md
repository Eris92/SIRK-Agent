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
- heartbeat i capability report,
- command dispatcher,
- polityki lokalne,
- bezpieczny katalog runtime,
- wersjonowanie i diagnostyka.

## Etap 2 - migracja Workspace

- `WorkspaceHost` pod kontrola SIRK-Agent,
- `captureFrame` bez dynamicznego PowerShell,
- pierwszy stabilny obraz DXGI,
- stale polaczenie capture,
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

Zbudowac minimalny, bezpieczny szkielet SIRK-Agent z heartbeat, IPC i obsluga `System.GetStatus`, a nastepnie podlaczyc do niego `Workspace.CaptureFrame`.
