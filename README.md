# SIRK Agent

Agent Windows rozwijany jako niezalezny komponent platformy SIRK. Pierwszy etap projektu obejmuje alternatywny modul zdalnego pulpitu dla MeshCentral, ale architektura docelowa obejmuje rowniez centralne zarzadzanie politykami, telemetrie bezpieczenstwa, tryb dochodzeniowy i odpornosc na manipulacje.

## Zasady architektoniczne

1. Agent nie ufa konfiguracji lokalnego hosta.
2. Polityki, aktualizacje i moduly musza byc podpisane przez zaufany system zarzadzania.
3. Brak telemetrii, utrata heartbeat albo niezgodnosc integralnosci sa zdarzeniami bezpieczenstwa.
4. Tryby rozszerzonego monitoringu musza miec Case ID, zakres, termin waznosci i audit.
5. Dane dowodowe musza miec spojny czas UTC, identyfikator urzadzenia, sesji, uzytkownika i lancuch integralnosci.

## Aktualny etap: Workspace

Po kliknieciu **Pulpit -New -> Polacz** system ma:

1. utworzyc sesje,
2. wyslac polecenie do wybranego urzadzenia,
3. uruchomic `WorkspaceHost` w sesji zalogowanego uzytkownika,
4. odebrac heartbeat,
5. pokazac PID, SessionId, uzytkownika i stan procesu,
6. uruchomic bezpieczny kanal IPC pomiedzy usluga i procesem uzytkownika,
7. przesylac obraz oraz obslugiwac wejscie bez zmiany oryginalnego modulu Desktop MeshCentral.

## Struktura docelowa

```text
MeshCentral-Plugin/
WorkspaceHost/
WorkspaceCommon/
SirkAgent.Service/
SirkAgent.Policy/
SirkAgent.Evidence/
SirkAgent.Activity/
SirkAgent.BrowserBridge/
docs/
schemas/
tests/
```

## Roadmap

### Faza A - Workspace

- v0.1 - szkielet repozytorium i dokumentacja
- v0.2 - uruchamianie WorkspaceHost przez MeshAgent
- v0.3 - heartbeat i diagnostyka
- v0.4 - bezpieczne IPC i ACL Named Pipe
- v0.5 - capture obrazu
- v0.6 - streaming obrazu
- v0.7 - input
- v0.8 - virtual display
- v1.0 - stabilny modul Pulpit -New

### Faza B - Security Foundation

- Policy Engine
- podpisane paczki polityk
- przypisanie polityki do Tenant ID i Device ID
- ochrona przed replay i rollback
- bezpieczny lokalny cache polityki
- certyfikat urzadzenia i klucz nieeksportowalny
- heartbeat oraz Tamper Detection
- podpisane aktualizacje i moduly

### Faza C - Evidence and Activity

- Evidence Engine z lancuchem hashy
- aktywne procesy, okna i czas aktywnosci
- metadane schowka
- operacje na plikach, USB, drukowanie i archiwizacja
- telemetria myszy i dynamiki klawiatury bez domyslnego zapisu tresci
- integracja Windows UI Automation
- screenshot na zdarzenie oraz ograniczony stream dochodzeniowy

### Faza D - Investigation and Insider Risk

- czasowy Investigation Mode
- scenariusz Departing Employee / Insider Risk
- korelacja pobrania, kopiowania, kompresji, uploadu, wysylki i usuniecia
- integracja Edge/Chrome dla URL, upload i download
- korelacja z Microsoft Defender, Purview, Exchange, SharePoint i OneDrive
- raport dowodowy z osia czasu
- scoring ryzyka i wykrywanie anomalii operatora

## Dokumentacja

- [Roadmap Security](docs/ROADMAP-SECURITY.md)
- [Policy Engine](docs/POLICY-ENGINE.md)
- [Investigation i Insider Risk](docs/INVESTIGATION-INSIDER-RISK.md)
- [Tamper Protection](docs/TAMPER-PROTECTION.md)
- [Evidence Engine](docs/EVIDENCE-ENGINE.md)

Projekt jest rozwijany etapami. Oryginalny modul Desktop MeshCentral pozostaje bez zmian.