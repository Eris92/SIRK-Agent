# Wizja SIRK Management Platform

## Misja

Zbudowac bezpieczna, bardzo szybka i niezalezna platforme do zarzadzania urzadzeniami, systemami, uslugami i integracjami IT.

Platforma nie moze byc ograniczona do komputerow ani zalezec od jednego producenta, portalu lub transportu.

## Oficjalne nazewnictwo

- **SIRK Management Platform (SMP)** - caly produkt.
- **SIRK-Agent** - lokalny agent wykonawczy.
- **SIRK-Server** - broker, API, polityki, audyt i routing.
- **SIRK-Portal** - interfejs operatora.
- **SIRK-Protocol** - wersjonowany protokol komunikacji.
- **SIRK-MeshAdapter** - przejsciowa i opcjonalna integracja z MeshCentral.
- **Workspace** - modul zdalnego pulpitu, a nie nazwa calego produktu.

## Zasady nadrzedne

1. Security over features.
2. Performance over convenience.
3. Transport independence.
4. No vendor lock-in.
5. Minimal privilege.
6. Secure by default.
7. Self-healing with safe rollback.
8. Kazda operacja uprzywilejowana musi byc autoryzowana i audytowana.

## Cel migracyjny

Po wdrozeniu SIRK-Server administrator ma moc odlaczyc MeshCentral bez reinstalowania SIRK-Agent i bez utraty funkcji runtime.
