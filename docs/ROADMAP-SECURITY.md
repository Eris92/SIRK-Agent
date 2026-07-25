# SIRK Agent - Roadmap Security

## Cel

Rozbudowa agenta z modulu zdalnego pulpitu do centralnie zarzadzanego komponentu bezpieczenstwa, dochodzen i ochrony przed eksfiltracja danych.

## Priorytety

### P0 - Stabilizacja Workspace

- poprawny ACL Named Pipe pomiedzy LocalSystem i procesem uzytkownika,
- uwierzytelnienie obu koncow IPC,
- ograniczenie dostepu do wskazanego SID sesji,
- timeout, reconnect i jednoznaczna diagnostyka,
- capture, streaming i kontrolowane wejscie.

### P1 - Policy Engine

- format podpisanej paczki polityki,
- walidacja podpisu,
- Tenant ID, Device ID, Policy ID i Case ID,
- `notBefore`, `expiresAt`, `epoch`, `sequence`, `nonce`,
- ochrona przed replay i rollback,
- cache ostatniej prawidlowej polityki,
- atomowa aktywacja i bezpieczny rollback tylko przez podpisana polityke awaryjna.

### P2 - Tamper Protection

- kod podpisany cyfrowo,
- kontrola integralnosci plikow i modulow,
- certyfikat urzadzenia z kluczem nieeksportowalnym,
- heartbeat i stan zdrowia agenta,
- alert przy zatrzymaniu uslugi, braku telemetry lub zmianie binariow,
- brak lokalnych przelacznikow zmieniajacych polityke serwera.

### P3 - Evidence Engine

- normalizowany format zdarzen,
- monotoniczny numer sekwencji,
- `previousEventHash` i `eventHash`,
- podpis paczek zdarzen,
- szyfrowany bufor lokalny,
- wysylka do magazynu niezmiennego,
- eksport raportu wraz z manifestem integralnosci.

### P4 - Activity Collectors

- procesy i aktywne okna,
- idle i zmiany fokusu,
- telemetria myszy,
- timing klawiatury bez domyslnego zapisu znakow,
- metadane schowka,
- operacje na plikach,
- USB, drukowanie i archiwizacja,
- Windows UI Automation,
- screenshot wyzwalany zdarzeniem.

### P5 - Browser Bridge

- aktywna karta, domena i URL zgodnie z polityka,
- upload, download i drag-and-drop,
- wybor pliku i wynik uploadu,
- integracja z Edge i Chrome,
- ograniczenie danych do zakresu sprawy i polityki.

### P6 - Investigation Mode

- Case ID i formalne zatwierdzenia,
- wskazany uzytkownik, urzadzenie i sesja,
- scisly okres aktywnosci,
- automatyczne wygaszenie,
- szczegolowa os czasu,
- screenshot lub ograniczony stream tylko po wlaczeniu odpowiedniej funkcji.

### P7 - Insider Risk / Departing Employee

- formalny trigger HR lub Security,
- wykrywanie masowych pobran,
- korelacja pobranie -> archiwum -> upload -> usuniecie,
- prywatna poczta i chmury osobiste,
- USB i wydruk,
- porownanie z profilem bazowym,
- raport ryzyka i material dowodowy.

## Kolejnosc realizacji

1. Naprawa IPC i ACL Named Pipe.
2. Policy Engine MVP.
3. Device identity i podpis polityki.
4. Evidence Engine MVP.
5. Activity Collectors.
6. Browser Bridge.
7. Investigation Mode.
8. Insider Risk.
9. Behavioral Analytics i scoring.

## Kryterium ukonczenia Security Foundation

Agent akceptuje tylko prawidlowo podpisana polityke przypisana do swojego urzadzenia, odrzuca cofniecie wersji, raportuje brak lacznosci i zapisuje zdarzenia w sposob pozwalajacy wykryc modyfikacje.