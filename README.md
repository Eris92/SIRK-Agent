# MeshCentral Workspace

Alternatywny modul zdalnego pulpitu i zdalnego Workspace dla MeshCentral. Projekt jest rozwijany jako osobna wtyczka oraz natywny host Windows i nie modyfikuje oryginalnego modulu Desktop MeshCentral.

## Aktualny stan

Wersja pluginu: `0.8.9`.
Wersja `WorkspaceHost.exe`: `0.7.0`.

Dzialajace elementy:

- zakladka urzadzenia `Pulpit -New`,
- uruchamianie `WorkspaceHost.exe` przez MeshAgent,
- bootstrap procesu z SYSTEM do aktywnej sesji Windows,
- trzy sloty Workspace:
  - sesja uzytkownika,
  - Workspace A,
  - Workspace B,
- izolowane desktopy `SirK-Admin-1` i `SirK-Admin-2`,
- heartbeat JSON przez Named Pipe,
- diagnostyka procesu, sesji, desktopu, rozdzielczosci i GUI,
- lista okien na wybranym desktopie,
- uruchamianie Explorer, PowerShell, CMD, Notatnika i wskazanej aplikacji,
- wybor trybu Device Broker, USB Passthrough lub Virtual Media,
- techniczna wersja Virtual Media montujaca ISO z adresu HTTPS.

W przygotowaniu:

- DXGI capture i streaming obrazu,
- klawiatura i mysz,
- clipboard,
- upload ISO i biblioteka obrazow na serwerze MeshCentral,
- PIV / Smart Card Broker,
- USB Passthrough,
- wirtualny display i niezalezny kursor.

## Architektura

```text
Przegladarka
    -> MeshCentral Workspace Plugin
    -> MeshAgent
    -> WorkspaceHost.exe
    -> widoczny lub izolowany desktop Windows
```

Warstwy funkcjonalne:

```text
Workspace
├── Desktop / Capture
├── Input
├── Clipboard
├── Apps / Windows
├── Device Broker
│   └── PIV / Smart Card
├── USB Passthrough
└── Virtual Media
```

Szczegoly: `docs/ARCHITECTURE.md`.

## Struktura

```text
MeshCentral-Plugin/   plugin, backend sesji i UI
WorkspaceHost/        natywny proces Windows C++
WorkspaceCommon/      wspolne modele i kod prototypowy
docs/                 architektura, roadmapa i build
tools/                build oraz testy
.github/workflows/    CI i publikacja
AGENTS.md              instrukcje dla agentow kodujacych
```

## Build

Wymagane sa CMake oraz Visual Studio Build Tools 2022 z toolchainem C++.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\build.ps1 -Configuration Release -Runtime win-x64
```

Wyniki:

```text
artifacts\MeshCentral-Workspace-Plugin-<wersja>.zip
artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe
```

Szczegoly: `docs/BUILD.md`.

## Publikacja

- `develop` - biezacy rozwoj i zrodlo buildow,
- `plugin` - automatycznie publikowana zawartosc `MeshCentral-Plugin/`,
- `develop-latest` - prerelease z aktualnym `WorkspaceHost.exe` i ZIP pluginu,
- `main` - docelowa wersja stabilna.

Plugin pobiera runtime z release `develop-latest`, weryfikuje SHA-256 i wymienia plik tylko wtedy, gdy wersja hosta jest inna.

## Bezpieczenstwo

- kazda operacja na hoście musi przejsc kontrole praw MeshCentral,
- GUI Workspace administracyjnego pozostaje niewidoczne dla zwyklego uzytkownika,
- sekrety, hasla, PIN-y PIV i clipboard nie moga trafic do logow,
- PIV ma byc realizowane przez WinSCard/APDU Broker,
- USB Passthrough pozostaje osobnym, opcjonalnym trybem,
- duze pliki ISO beda przesylane strumieniowo i przechowywane w bibliotece serwera, a nie kodowane jako Base64.

## Dokumentacja

- `AGENTS.md` - zasady pracy i aktualny stan projektu,
- `docs/ARCHITECTURE.md` - komponenty i przeplywy,
- `docs/ROADMAP.md` - kolejne etapy,
- `docs/BUILD.md` - build, test i publikacja.
