# Media Broker

## Cel

Media Broker ma obslugiwac kamere, mikrofon, glosniki, snapshot, nagrywanie i transmisje WebRTC bez uzaleznienia funkcji od jednego frontendu. MeshCentral pozostaje pierwszym adapterem transportowym, ale modul ma byc mozliwy do wykorzystania takze przez przyszly serwer standalone.

## Tryby

### Incident Evidence

Domyslnie wylaczony. Uruchamiany recznie dla konkretnego incydentu i tylko przez operatora z osobnym uprawnieniem.

Planowany zakres:

- snapshot z kamery,
- krotkie nagranie wideo,
- krotkie nagranie audio,
- screenshot lub nagranie pulpitu,
- procesy, sesje, polaczenia sieciowe i logi,
- manifest operacji,
- SHA-256 wszystkich artefaktow,
- pakiet dowodowy ZIP.

Wymagane pola operacji:

- identyfikator incydentu,
- powod,
- operator,
- host i Workspace,
- czas rozpoczecia i zakonczenia,
- zakres zebranych danych.

### Home Monitor

Profil przeznaczony tylko dla prywatnego, swiadomie skonfigurowanego urzadzenia domowego.

Planowany zakres:

- podglad kamery na zywo,
- dzwiek na zywo,
- rozmowa dwukierunkowa,
- snapshot,
- nagrywanie,
- wykrywanie ruchu,
- powiadomienia.

Transmisja na zywo ma korzystac z WebRTC. Serwer odpowiada za uwierzytelnianie, sygnalizacje, uprawnienia, audyt oraz opcjonalny TURN relay.

## Polityka bezpieczenstwa

- wszystkie funkcje capture sa domyslnie wylaczone,
- enumeracja urzadzen nie uruchamia kamery ani mikrofonu,
- capture wymaga osobnego uprawnienia `MediaCapture`,
- tryb Incident wymaga identyfikatora incydentu i uzasadnienia,
- tryb Home musi byc jawnie przypisany do prywatnego urzadzenia,
- sprzetowa sygnalizacja kamery i mikrofonu nie moze byc obchodzona,
- kazde uzycie jest zapisywane w audycie,
- sesje maja limit czasu i automatyczne rozlaczenie,
- nagrania sa szyfrowane i maja polityke retencji,
- sam podglad nie zapisuje nagrania bez osobnej decyzji,
- funkcja nie moze byc aktywowana globalnie przez przypadek.

## Architektura

```text
Browser / Mobile
        -> MeshCentral Adapter lub Standalone Server
        -> Workspace Media Broker
        -> WorkspaceAgent / WorkspaceHost
        -> Camera / Microphone / Speaker / Desktop
```

Warstwy:

```text
WorkspaceMediaCore
WorkspaceMediaPolicy
WorkspaceMediaTransport
MeshCentralMediaAdapter
StandaloneMediaAdapter
WebRTC Signaling
IncidentEvidenceStore
```

## Etapy implementacji

1. enumeracja kamer, mikrofonow i glosnikow,
2. polityka per host i profil,
3. snapshot z kamery z widocznym statusem,
4. nagranie audio i wideo z limitem czasu,
5. zapis audytu i manifestu,
6. pakiet Incident Evidence,
7. WebRTC live video/audio,
8. talk-back,
9. profil Home Monitor,
10. detekcja ruchu i powiadomienia.

## Stan 0.9.3

Zaimplementowano pierwszy bezpieczny fundament:

- enumeracja kamer,
- enumeracja mikrofonow,
- enumeracja glosnikow,
- transport wyniku przez MeshAgent,
- panel Media Broker w UI,
- jawna polityka: capture, recording i streaming sa wylaczone.

Wersja 0.9.3 nie wykonuje zdjec, nie nagrywa i nie uruchamia transmisji.