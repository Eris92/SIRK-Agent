# Kontynuacja projektu SIRK Agent w nowym czacie

Ten dokument jest aktualnym punktem przekazania projektu. W nowym czacie nalezy wskazac repozytorium i poprosic o przeczytanie tego pliku przed wykonaniem kolejnych zmian.

## Identyfikacja projektu

- Repozytorium: `Eris92/SIRK-Agent`
- Glowny branch: `main`
- Platforma docelowa: Windows x64
- Runtime: .NET 8, build framework-dependent
- Zasada pakowania: nie dolaczac calego .NET do EXE ani ZIP
- Aktualny etap: `0.3.5-test` — Endurance Worker, raporty dlugotrwale i odzyskiwanie uslugi przez SCM
- Ostatni potwierdzony stabilny pakiet testowy na komputerze `DELL_K`: `0.3.5-test`, commit `f208f0ec58dd9545c7f279b9701ddef0bd0f2a5c`
- Aktualny commit przekazania: sprawdz najnowszy HEAD `main` przed rozpoczeciem pracy

## Cel projektu

SIRK Agent jest niezaleznym agentem Windows dla platformy SIRK. Laczy bezpieczne egzekwowanie centralnych polityk, ochrone przed manipulacja, diagnostyke, telemetrie offline, lancuch dowodowy oraz docelowo funkcje zdalnego zarzadzania, EDR/XDR i dochodzeniowe.

## Fundamentalne zasady bezpieczenstwa

1. Agent nie ufa konfiguracji lokalnego hosta.
2. Polityki, Recovery i przyszle aktualizacje musza byc podpisane.
3. Polityka jest przypisana do Tenant ID i Device ID oraz zawiera Policy ID, epoch, version, nonce i okres waznosci.
4. Replay i rollback sa blokowane.
5. Chroniony stan lokalny korzysta z DPAPI LocalMachine i atomowego zapisu.
6. Manipulacja stanem, utrata integralnosci i kwarantanna musza byc raportowane fail-closed.
7. Wyjscie z kwarantanny jest dozwolone tylko przez podpisana Recovery Policy.
8. Testowy klucz `test-signing-key.pem` nie jest kluczem produkcyjnym.
9. Pakiet pozostaje framework-dependent dla .NET 8 x64.

## Aktualnie dzialajace elementy

### Agent Core i Windows Service

- instalacja, aktualizacja i deinstalacja uslugi `SirkAgent`,
- Automatic Start,
- recovery SCM po awarii procesu,
- `AgentWorker`, `ManagementWorker`, `ManagementStateReconciler`, `RuntimeHealthWorker`, `EnduranceWorker` i lokalny Control Worker,
- trwaly Device ID chroniony DPAPI,
- Scheduler i FileSystemWatcher,
- centralna Security State Machine,
- Health Monitor i rejestr modulow.

### Policy Management

- podpis ES256,
- Tenant ID i Device ID binding,
- kontrola epoch, version, nonce i czasu waznosci,
- ochrona przed replay i rollback,
- podpisana Recovery Policy,
- katalogi `Incoming`, `Archive\Accepted`, `Archive\Rejected` i `Archive\Recovery`,
- trwaly `active-policy.json`,
- odbudowa `management-state.json` po restarcie z aktywnej polityki i archiwow.

### Tamper, Quarantine i Evidence

- kontrola integralnosci `policy-state.bin`,
- trwala kwarantanna chroniona DPAPI,
- fail-closed po uszkodzeniu stanu,
- Evidence Chain z lancuchem hashy,
- walidacja lancucha przy kazdym cyklu,
- zdarzenia i aktualny status w JSON.

### Telemetry Queue

- kolejka offline chroniona DPAPI,
- retry i backoff transportu,
- limit 50 MB,
- limit 5000 plikow,
- retencja 14 dni dla zdarzen niekrytycznych,
- throttling zwyklych cykli do okolo jednego wpisu na 5 minut,
- `sirkctl queue-status`,
- testowe czyszczenie tylko z jawnym `--confirm-test-clear`.

### Lokalny interfejs zarzadzania

CLI `sirkctl.exe` obsluguje:

- `enroll --endpoint <url> --bootstrap-token-file <path>` — jednorazowa rejestracja
  urządzenia, zapis tokenu per-device przez DPAPI LocalMachine,
- `sync` — żądanie check-in do SIRK Portal (przez fallback plikowy wykonanie
  następuje w cyklu do 15 sekund),

```text
status
process
flush
queue-status
queue-clear-test --confirm-test-clear
verify-integrity
create-test-policy
create-test-recovery
```

Named Pipe zwraca dokladnie jeden kompletny dokument JSON. Przy niedostepnym pipe dziala kontrolowany fallback plikowy.

### Diagnostyka runtime

Plik:

```text
C:\ProgramData\SIRK\Agent\runtime-health.json
```

Zawiera m.in.:

- PID i uptime,
- CPU,
- Working Set, Private Memory i pamiec zarzadzana,
- liczbe watkow i uchwytow,
- wiek i swiezosc heartbeat,
- rozmiar logu zdarzen.

`agent-events.jsonl` jest rotowany po 10 MB. Zachowywanych jest piec archiwow.

### Endurance — etap 0.3.5-test

Nowy `EnduranceWorker` zapisuje:

```text
C:\ProgramData\SIRK\Agent\endurance-samples.jsonl
C:\ProgramData\SIRK\Agent\endurance-summary.json
C:\ProgramData\SIRK\Agent\endurance-report.html
```

Funkcje:

- probka domyslnie co 5 minut,
- maksymalnie 576 probek, czyli 48 godzin,
- CPU i RAM min/avg/max,
- trend RAM na godzine,
- wykrywanie podejrzenia wycieku,
- liczenie restartow procesu,
- wykrywanie przerw w probkowaniu,
- liczenie niezdrowych probek,
- rozmiar Telemetry Queue, Evidence Chain i logu,
- filtrowanie niekompletnych probek rozruchowych,
- raport JSON i HTML.

## Potwierdzone testy na DELL_K

Wersje do `0.3.5-test` zostaly zweryfikowane na rzeczywistym Windows:

- `sirkctl status` zwraca pelny JSON,
- podpisana polityka przechodzi `Incoming -> active-policy.json -> Archive\Accepted`,
- Device ID pozostaje staly,
- stan polityki i liczniki pozostaja po restarcie,
- piec kolejnych restartow uslugi zakonczylo sie stanem `Operational / Healthy`,
- kazdy restart otrzymal nowy PID,
- heartbeat po kazdym restarcie byl swiezy,
- CPU w spoczynku praktycznie 0%,
- RAM po restartach okolo 41–42 MB,
- `runtime-health.json` aktualizuje sie prawidlowo.
- aktualizacja z `0.3.4-test` zachowala Device ID, aktywna polityke i stan chroniony,
- recovery SCM przywrocilo usluge po wymuszonym zakonczeniu procesu,
- lokalny SIRK Portal odebral uwierzytelniony check-in z heartbeat i runtime health,
- ACL `C:\ProgramData\SIRK\Agent` blokuje zapis zwyklym uzytkownikom,
- Endurance Report CI i wszystkie workflow regresyjne dla `f208f0e` sa zielone.

Device ID komputera testowego:

```text
cb0bfc0d-4376-4f42-b781-6dc2be0405e9
```

## Aktualny stan prac 0.3.5-test

Kod Endurance Worker, raportow i workflow znajduje sie na `main`.

Ostatnio poprawione problemy:

- blad kompilatora `CS9006` w interpolowanym raw stringu HTML — generator przepisany na `StringBuilder`,
- niekompletne probki z pierwszych sekund startu nie sa wliczane do statystyk endurance.
- wyscig inicjalizacji `policy-state.bin` pomiedzy AgentWorker i ManagementWorker zostal usuniety przez serializowany zapis atomowy,
- instalator usuwa odziedziczone prawo zapisu zwyklych uzytkownikow do katalogu danych Agenta.

Przed przygotowaniem paczki nalezy:

1. sprawdzic najnowszy HEAD `main`,
2. sprawdzic wynik workflow `SIRK Agent Endurance Report CI`,
3. potwierdzic utworzenie czterech przyspieszonych probek,
4. potwierdzic `endurance-summary.json` i `endurance-report.html`,
5. zabic proces uslugi i potwierdzic recovery SCM,
6. potwierdzic zapis zmiany PID jako restartu,
7. uruchomic pozostale workflow regresyjne,
8. dopiero po zielonym wyniku pobrac artefakt `SIRK-Agent-0.3.5-test-win-x64`.

Powyższe kryteria sa spelnione dla commita `f208f0e`. Kazdy kolejny commit
zmieniajacy runtime lub pakowanie wymaga ponownego pelnego CI i testu lokalnego.

## Najwazniejsze pliki runtime

```text
C:\ProgramData\SIRK\Agent\device-identity.bin
C:\ProgramData\SIRK\Agent\policy-state.bin
C:\ProgramData\SIRK\Agent\active-policy.json
C:\ProgramData\SIRK\Agent\management-state.json
C:\ProgramData\SIRK\Agent\heartbeat-latest.json
C:\ProgramData\SIRK\Agent\security-state.json
C:\ProgramData\SIRK\Agent\runtime-health.json
C:\ProgramData\SIRK\Agent\quarantine-state.bin
C:\ProgramData\SIRK\Agent\quarantine-status.json
C:\ProgramData\SIRK\Agent\evidence-events.jsonl
C:\ProgramData\SIRK\Agent\evidence-state.bin
C:\ProgramData\SIRK\Agent\agent-events.jsonl
C:\ProgramData\SIRK\Agent\TelemetryQueue\
C:\ProgramData\SIRK\Agent\Archive\Accepted\
C:\ProgramData\SIRK\Agent\Archive\Rejected\
C:\ProgramData\SIRK\Agent\endurance-samples.jsonl
C:\ProgramData\SIRK\Agent\endurance-summary.json
C:\ProgramData\SIRK\Agent\endurance-report.html
```

## Polecenia operatorskie

Instalacja lub aktualizacja:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
.\Install-SirkAgent.ps1
```

Status:

```powershell
.\sirkctl.exe status
.\sirkctl.exe queue-status
.\sirkctl.exe verify-integrity
```

Test polityki:

```powershell
.\sirkctl.exe create-test-policy
.\sirkctl.exe process
```

Podglad endurance:

```powershell
Get-Content "C:\ProgramData\SIRK\Agent\endurance-summary.json" -Raw |
    ConvertFrom-Json |
    Format-List

Start-Process "C:\ProgramData\SIRK\Agent\endurance-report.html"
```

## Zasady kontynuowania prac

- Kontynuowac autonomicznie do stabilnego, testowalnego etapu.
- Po zmianach uruchamiac odpowiedni CI oraz pelny pipeline regresyjny.
- Nie wydawac paczki po czesciowo zielonych testach.
- Nie resetowac Device ID, aktywnej polityki, Evidence Chain ani kwarantanny podczas zwyklej aktualizacji.
- Nie uzywac testowego klucza podpisu jako rozwiazania produkcyjnego.
- Zachowac kompatybilnosc z Windows PowerShell 5.1 dla skryptow operatorskich.
- W odpowiedziach dla testera podawac gotowe polecenia PowerShell.

## Polecenie do wznowienia w nowym oknie

Skopiuj do nowego czatu dokladnie ponizsza wiadomosc:

```text
Kontynuuj projekt SIRK Agent z repozytorium GitHub Eris92/SIRK-Agent na branchu main. Najpierw przeczytaj w calosci docs/CONTINUE-IN-NEW-CHAT.md oraz README.md i sprawdz najnowszy HEAD main oraz aktualne wyniki GitHub Actions. Projekt jest na etapie 0.3.5-test: EnduranceWorker, endurance-samples.jsonl, endurance-summary.json, endurance-report.html, trend RAM i recovery uslugi przez SCM. Ostatni potwierdzony stabilny pakiet na DELL_K to 0.3.4-test. Nie uznawaj 0.3.5-test za gotowy, dopoki SIRK Agent Endurance Report CI oraz pozostale testy regresyjne nie beda zielone. Kontynuuj autonomicznie: zdiagnozuj nieudany krok, popraw kod lub workflow, zrob commit bezposrednio do main, uruchom pelne CI, a po sukcesie pobierz i przekaz paczke Windows x64. Nie pakuj calego .NET do ZIP, zachowaj framework-dependent .NET 8, nie resetuj Device ID ani chronionego stanu przy aktualizacji i podawaj kompletne polecenia PowerShell do testow.
```

## Dokumenty powiazane

- `README.md`
- `docs/AGENT-CORE-ARCHITECTURE.md`
- `docs/POLICY-ENGINE.md`
- `docs/TAMPER-PROTECTION.md`
- `docs/EVIDENCE-ENGINE.md`
- `docs/INVESTIGATION-INSIDER-RISK.md`
- `docs/ROADMAP-SECURITY.md`

Ten dokument powinien byc aktualizowany przy kazdym wiekszym etapie lub wydaniu paczki testowej.
