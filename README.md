# MeshCentral Workspace

Alternatywny modul zdalnego pulpitu dla MeshCentral rozwijany jako osobna wtyczka i host Windows.

## Cel pierwszego etapu

Po kliknieciu **Pulpit -New -> Polacz** wtyczka ma:

1. utworzyc sesje,
2. wyslac polecenie do wybranego urzadzenia,
3. uruchomic `WorkspaceHost` w sesji zalogowanego uzytkownika,
4. odebrac heartbeat,
5. pokazac PID, SessionId, uzytkownika i stan procesu.

## Struktura

```text
MeshCentral-Plugin/
WorkspaceHost/
WorkspaceCommon/
docs/
tests/
```

## Roadmap

- v0.1 - szkielet repozytorium i dokumentacja
- v0.2 - uruchamianie WorkspaceHost przez MeshAgent
- v0.3 - heartbeat i diagnostyka
- v0.4 - DXGI capture
- v0.5 - streaming obrazu
- v0.6 - input
- v0.7 - virtual display
- v1.0 - stabilny modul Pulpit -New

Projekt jest rozwijany etapami. Oryginalny modul Desktop MeshCentral pozostaje bez zmian.
