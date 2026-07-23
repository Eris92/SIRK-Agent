# SIRK Management Platform

SIRK Management Platform (SMP) to bezpieczna, szybka i niezalezna platforma do zarzadzania srodowiskami IT oraz integracji z innymi systemami.

MeshCentral nie jest juz traktowany jako docelowy rdzen produktu. Pozostaje pierwszym adapterem wdrozeniowym i transportowym, ktory instaluje oraz uruchamia `SIRK-Agent` na zarzadzanych urzadzeniach.

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

## Model przejsciowy

```text
MeshCentral Plugin / MeshAgent
        ↓ instalacja i transport przejsciowy
SIRK-Agent
        ↓
SIRK Runtime
├── Workspace
├── Terminal
├── Files
├── Registry
├── Software
├── Automation
├── Monitoring
└── Security
```

Po uruchomieniu SIRK-Server ten sam agent ma polaczyc sie z nowym portalem bez reinstalacji i bez przepisywania modulow wykonawczych.

## Najblizszy etap

- fundament uslugi Windows `SIRK-Agent`,
- wersjonowany protokol polecen,
- lokalne IPC przez Named Pipe,
- instalacja oraz aktualizacja przez MeshAgent,
- heartbeat i diagnostyka,
- przeniesienie `captureFrame` pod kontrole agenta,
- przygotowanie transportu standalone do przyszlego SIRK-Server.

Szczegoly znajduja sie w katalogu [`docs`](docs/README.md).
