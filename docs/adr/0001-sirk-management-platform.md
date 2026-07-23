# ADR-0001: SIRK Management Platform zamiast pluginu zależnego od MeshCentral

- Status: Accepted
- Data: 2026-07-23

## Kontekst

Projekt rozpoczal sie jako alternatywny modul zdalnego pulpitu dla MeshCentral. Kolejne wymagania obejmuja terminal, pliki, automatyzacje, monitoring, bezpieczenstwo i integracje z systemami innymi niz komputery.

Rozwijanie calej logiki wewnatrz pluginu utrudniloby migracje do wlasnego portalu, zwiekszyloby zaleznosc od MeshCentral oraz pogorszylo bezpieczenstwo i wydajnosc przez dynamiczne polecenia i dodatkowe warstwy transportu.

## Decyzja

Budujemy SIRK Management Platform. `SIRK-Agent` jest niezaleznym agentem wykonawczym. MeshCentral pozostaje pierwszym adapterem instalacyjnym, transportowym i awaryjnym.

Workspace jest modulem platformy, a nie nazwa calego produktu.

## Konsekwencje

Pozytywne:

- mozliwosc podlaczenia agenta do SIRK-Server bez reinstalacji,
- wspolny runtime dla Mesha, portalu, API i SDK,
- latwiejsze testowanie, audyt i kontrola uprawnien,
- lepsza wydajnosc przez staly proces i bezposrednie IPC,
- mozliwosc integracji z wieloma typami systemow.

Koszty:

- koniecznosc zbudowania protokolu, instalatora, aktualizatora i serwera,
- wiekszy naklad na kompatybilnosc oraz bezpieczenstwo kontraktow,
- przejsciowy okres utrzymywania dwoch transportow.
