# Dokumentacja SIRK Management Platform

Ta dokumentacja jest konstytucja projektu. Kazda duza zmiana architektoniczna musi byc oceniona wzgledem bezpieczenstwa, wydajnosci, niezaleznosci transportowej oraz mozliwosci przyszlej integracji z innymi systemami.

## Dokumenty

- [00-VISION.md](00-VISION.md) - misja, zakres i zasady produktu.
- [01-ARCHITECTURE.md](01-ARCHITECTURE.md) - komponenty i granice odpowiedzialnosci.
- [02-SECURITY.md](02-SECURITY.md) - model Zero Trust i wymagania bezpieczenstwa.
- [03-PERFORMANCE.md](03-PERFORMANCE.md) - cele dla slabych i niestabilnych laczy.
- [04-PROTOCOL.md](04-PROTOCOL.md) - fundament SIRK Protocol.
- [05-ROADMAP.md](05-ROADMAP.md) - plan migracji od pluginu do niezaleznej platformy.
- [adr/0001-sirk-management-platform.md](adr/0001-sirk-management-platform.md) - decyzja o zmianie kierunku projektu.

## Filtr dla nowych funkcji

Przed akceptacja zmiany nalezy odpowiedziec:

1. Czy zmiana jest bezpieczna i audytowalna?
2. Czy pozostanie szybka na slabym laczu?
3. Czy nie uzaleznia modulu od MeshCentral albo innego pojedynczego produktu?
4. Czy mozna ja aktualizowac i wycofac bez utraty dostepu do urzadzenia?

Brak pozytywnej odpowiedzi oznacza koniecznosc zmiany projektu rozwiazania.
