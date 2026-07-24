# SIRK-Agent

To repozytorium jest glownym repozytorium wykonawczym agenta dla **SIRK Management Platform (SMP)**.

Oficjalna nazwa repozytorium: `Eris92/SIRK-Agent`.

SIRK Management Platform to bezpieczna, szybka i niezalezna platforma do zarzadzania srodowiskami IT oraz integracji z innymi systemami.

MeshCentral nie jest docelowym rdzeniem produktu. Pozostaje pierwszym adapterem wdrozeniowym i transportowym, ktory instaluje oraz uruchamia `SIRK-Agent` na zarzadzanych urzadzeniach.

## Zakres tego repozytorium

```text
SIRK-Agent/
├── SIRK-Agent/              # usluga Windows i lokalny runtime
├── SIRK-MeshAdapter/        # przejsciowy adapter MeshCentral -> SIRK Protocol
├── docs/                    # konstytucja i dokumentacja architektury
└── .github/workflows/       # build, testy i artefakty win-x64
```

Docelowo pozostale komponenty platformy, takie jak `SIRK-Portal`, `SIRK-Server`, `SIRK-Protocol` i `SIRK-SDK`, moga zostac wydzielone do osobnych repozytoriow. Kod agenta nie moze jednak zalezec od konkretnego transportu ani portalu.

## Priorytety projektu

1. **Security First** - zadna funkcja nie moze obchodzic walidacji, autoryzacji, audytu ani kontroli integralnosci.
2. **Performance First** - platforma ma pozostawac responsywna takze przy bardzo slabym i niestabilnym laczu.
3. **Transport Independence** - moduly wykonawcze nie moga zalezec od MeshCentral ani od konkretnego portalu.
4. **No Vendor Lock-in** - kazdy adapter i transport musi byc wymienialny.
5. **Enterprise Ready** - RBAC, MFA, audyt, polityki, HA i bezpieczne aktualizacje sa uwzgledniane od poczatku.

## Docelowa architektura

```text
SIRK Management Platform
├── SIRK-Portal
├── SIRK-Server
├── SIRK-Agent
├── SIRK-Protocol
├── SIRK-SDK
├── SIRK-MeshAdapter
├── SIRK-Installer
├── SIRK-Updater
└── SIRK-Diagnostics
```

## Aktualny przeplyw

```text
MeshCentral / narzedzie diagnostyczne
        ↓
SIRK-MeshAdapter
        ↓ SIRK Protocol v1
Named Pipe: SIRK.Agent.v1
        ↓
SIRK-Agent
        ↓
moduly System / Workspace / kolejne moduly
```

## Aktualny stan

- usluga Windows `.NET 8`,
- lokalne IPC przez Named Pipe,
- SIRK Protocol v1,
- UUID, TTL, nonce i ochrona anty-replay,
- `System.Ping`, `System.GetStatus`, `System.GetCapabilities`,
- klient diagnostyczny IPC,
- instalator i deinstalator,
- pierwszy `SIRK-MeshAdapter`,
- testy integracyjne Agent -> IPC oraz Adapter -> Agent,
- automatyczny build i publikacja artefaktow win-x64.

## Najblizszy etap

- modulowy dispatcher polecen,
- fundament modulu `Workspace`,
- kontrolowane `Workspace.GetCapabilities`,
- migracja `Workspace.CaptureFrame` bez dynamicznego PowerShell,
- jawne ACL Named Pipe dla SYSTEM, administratorow i autoryzowanego adaptera.

Szczegoly znajduja sie w katalogu [`docs`](docs/README.md).
