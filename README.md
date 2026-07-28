# SIRK Agent

Agent Windows rozwijany jako niezalezny komponent platformy SIRK. Projekt laczy bezpieczne egzekwowanie centralnych polityk, ochrone przed manipulacja, diagnostyke, telemetrie offline, lancuch dowodowy oraz docelowo zdalne zarzadzanie i funkcje klasy EDR/XDR.

## Kontynuacja projektu

Aktualnym dokumentem przekazania do nowego czatu jest:

- [Kontynuacja projektu w nowym czacie](docs/CONTINUE-IN-NEW-CHAT.md)

Dokument zawiera aktualny stan `main`, potwierdzone testy na Windows, stan prac `0.3.5-test`, pliki runtime, zasady bezpieczenstwa oraz gotowe polecenie do wznowienia pracy.

Glowny branch:

```text
main
```

Aktualny etap:

```text
0.3.5-test — Endurance Worker, raporty 24/48 h i recovery SCM
```

Ostatni potwierdzony stabilny pakiet testowy na komputerze `DELL_K`:

```text
0.3.5-test — commit f208f0ec58dd9545c7f279b9701ddef0bd0f2a5c
```

## Zasady architektoniczne

1. Agent nie ufa konfiguracji lokalnego hosta.
2. Polityki, Recovery, aktualizacje i moduly musza byc podpisane.
3. Polityki sa przypisane do Tenant ID i Device ID.
4. Replay i rollback musza byc blokowane.
5. Stan lokalny jest chroniony DPAPI LocalMachine i zapisywany atomowo.
6. Manipulacja i utrata integralnosci sa zdarzeniami bezpieczenstwa.
7. Wyjscie z kwarantanny jest mozliwe tylko przez podpisana Recovery Policy.
8. Nie dolaczamy calego .NET do EXE ani ZIP. Build Windows x64 pozostaje framework-dependent dla .NET 8.
9. Testowy klucz podpisu nie jest rozwiazaniem produkcyjnym.

## Aktualny stan implementacji

Dzialajace elementy:

- Windows Service `SirkAgent` z Automatic Start i recovery SCM,
- trwaly Device Identity chroniony DPAPI,
- Security State Machine,
- Scheduler i FileSystemWatcher,
- Health Monitor i rejestr modulow,
- Policy Engine z podpisem ES256,
- Tenant ID, Device ID, epoch, version i nonce,
- ochrona przed replay i rollback,
- podpisana Recovery Policy,
- trwaly Quarantine Mode,
- Evidence Chain z lancuchem hashy,
- offline Telemetry Queue z retencja i throttlingiem,
- integralnosc plikow runtime SHA-256,
- lokalne API Named Pipe i fallback plikowy,
- CLI `sirkctl`,
- raport diagnostyczny HTML/JSON,
- TestBundle,
- runtime health: CPU, RAM, uptime, heartbeat i rotacja logow,
- Endurance Worker z probkami, podsumowaniem JSON i raportem HTML.
- uwierzytelniony check-in do SIRK Portal z heartbeat, runtime health i telemetria,
- rejestracja per-device z tokenem bootstrap odczytywanym z pliku i poświadczeniem chronionym DPAPI LocalMachine,
- pobieranie kolejek podpisanych polityk dla konkretnego tenant/device oraz potwierdzenie po aktywacji,
- zabezpieczony ACL katalogu danych: zapis tylko dla SYSTEM i Administratorow.

## Najwazniejsze polecenia

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

Rejestracja urządzenia w SIRK Portal (PowerShell uruchomiony jako Administrator):

```powershell
$TokenFile = 'C:\Temp\sirk-enrollment-token.txt'
Set-Content -LiteralPath $TokenFile -Value '<TOKEN_BOOTSTRAP_Z_PORTALU>' -Encoding UTF8
& 'C:\Program Files\SIRK Agent\sirkctl.exe' enroll `
  --endpoint 'https://portal.example/api/agent/v1/enroll' `
  --bootstrap-token-file $TokenFile
& 'C:\Program Files\SIRK Agent\sirkctl.exe' sync
Remove-Item -LiteralPath $TokenFile -Force
```

Agent zapisuje wydany token urządzenia wyłącznie w
`C:\ProgramData\SIRK\Agent\portal-credential.bin`, chronionym przez DPAPI
LocalMachine i ACL katalogu danych. Portal przechowuje tylko hash SHA-256 tokenu.
HTTP jest akceptowany wyłącznie dla testów na adresie loopback.

Test podpisanej polityki:

```powershell
.\sirkctl.exe create-test-policy
.\sirkctl.exe process
```

Podglad raportu endurance:

```powershell
Get-Content "C:\ProgramData\SIRK\Agent\endurance-summary.json" -Raw |
    ConvertFrom-Json |
    Format-List

Start-Process "C:\ProgramData\SIRK\Agent\endurance-report.html"
```

## Pliki runtime

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

## Potwierdzone testy na DELL_K

- pelny `sirkctl status`,
- aktywacja podpisanej polityki,
- archiwizacja zaakceptowanej polityki,
- zachowanie Device ID i aktywnej polityki po restarcie,
- zachowanie licznikow Management State,
- pojedyncza odpowiedz JSON z Named Pipe,
- retencja i czyszczenie Telemetry Queue,
- runtime health,
- piec kolejnych restartow uslugi,
- po kazdym restarcie `Operational`, `Healthy` i swiezy heartbeat,
- RAM po restartach okolo 41–42 MB,
- CPU w spoczynku praktycznie 0%.
- aktualizacja `0.3.4-test -> 0.3.5-test` bez zmiany Device ID i aktywnej polityki,
- recovery SCM po wymuszonym zakonczeniu procesu,
- rzeczywisty check-in `0.3.5-test` do lokalnego SIRK Portal,
- brak prawa zapisu do katalogu danych dla zwyklych uzytkownikow.

## Aktualny etap 0.3.5-test

Kod Endurance Worker jest na `main`. Pakiet z commita `f208f0e` przeszedl:

```text
SIRK Agent Endurance Report CI
```

oraz pozostale workflow regresyjne. Lokalnie na DELL_K potwierdzono instalacje,
zachowanie chronionego stanu, recovery SCM, ACL danych i check-in do SIRK Portal.

## Struktura repozytorium

```text
src/
  SirkAgent.Policy/
  SirkAgent.Service/
  SirkAgent.Report/
  SirkAgent.Cli/
tools/
  package/
tests/
docs/
schemas/
.github/workflows/
```

## Dokumentacja

- [Kontynuacja projektu w nowym czacie](docs/CONTINUE-IN-NEW-CHAT.md)
- [Agent Core Architecture](docs/AGENT-CORE-ARCHITECTURE.md)
- [Roadmap Security](docs/ROADMAP-SECURITY.md)
- [Policy Engine](docs/POLICY-ENGINE.md)
- [Investigation i Insider Risk](docs/INVESTIGATION-INSIDER-RISK.md)
- [Tamper Protection](docs/TAMPER-PROTECTION.md)
- [Evidence Engine](docs/EVIDENCE-ENGINE.md)

## Polecenie do wznowienia w nowym oknie

```text
Kontynuuj projekt SIRK Agent z repozytorium GitHub Eris92/SIRK-Agent na branchu main. Najpierw przeczytaj w calosci docs/CONTINUE-IN-NEW-CHAT.md oraz README.md i sprawdz najnowszy HEAD main oraz aktualne wyniki GitHub Actions. Projekt jest na etapie 0.3.5-test: EnduranceWorker, endurance-samples.jsonl, endurance-summary.json, endurance-report.html, trend RAM i recovery uslugi przez SCM. Ostatni potwierdzony stabilny pakiet na DELL_K to 0.3.4-test. Nie uznawaj 0.3.5-test za gotowy, dopoki SIRK Agent Endurance Report CI oraz pozostale testy regresyjne nie beda zielone. Kontynuuj autonomicznie: zdiagnozuj nieudany krok, popraw kod lub workflow, zrob commit bezposrednio do main, uruchom pelne CI, a po sukcesie pobierz i przekaz paczke Windows x64. Nie pakuj calego .NET do ZIP, zachowaj framework-dependent .NET 8, nie resetuj Device ID ani chronionego stanu przy aktualizacji i podawaj kompletne polecenia PowerShell do testow.
```
