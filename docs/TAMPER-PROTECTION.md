# Tamper Protection

## Zasada

Agent nie moze przyjmowac lokalnych zmian konfiguracji, ktore wylaczaja monitoring, blokady albo kontrole integralnosci. Ustawienia runtime pochodza tylko z prawidlowo podpisanej polityki serwera.

## Warstwy ochrony

### Tozsamosc urzadzenia

- indywidualny certyfikat urzadzenia,
- klucz prywatny nieeksportowalny,
- preferowane powiazanie z TPM,
- wzajemne TLS z serwerem zarzadzania,
- polityka przypisana do konkretnego Device ID.

### Integralnosc kodu

- podpisane binaria,
- allowlista zaufanych wydawcow i modulow,
- hash manifestu instalacji,
- sprawdzanie integralnosci przy starcie i cyklicznie,
- alert przy podmianie lub braku pliku.

### Ochrona konfiguracji

- brak lokalnych przelacznikow zmieniajacych polityke,
- szyfrowany i uwierzytelniony cache,
- wykrywanie edycji, usuniecia oraz cofniecia wersji,
- atomowy zapis aktywnej polityki,
- bezpieczny tryb bazowy po bledzie walidacji.

### Ochrona uslugi

- automatyczny restart,
- watchdog niezalezny od glownego procesu,
- heartbeat do serwera,
- rejestrowanie zatrzymania, awarii i dlugiego braku lacznosci,
- integracja z zewnetrznym EDR w celu wykrycia manipulacji.

## Granica techniczna

Proces z uprawnieniami kernel lub pelna kontrola SYSTEM moze probowac zatrzymac, podmienic albo oszukac komponent lokalny. Celem jest maksymalne utrudnienie, szybkie wykrycie i zachowanie niezaleznego sladu po stronie serwera, a nie deklarowanie absolutnej niemozliwosci manipulacji.

## Fail-safe

- blokady pozostaja aktywne przy utracie serwera,
- polityka czasowa wygasa zgodnie z podpisanym `expiresAtUtc`,
- agent nie przyjmuje konfiguracji z rejestru ani pliku awaryjnego,
- kolejka zdarzen jest szyfrowana i wysylana po odzyskaniu lacznosci,
- brak danych jest traktowany jako zdarzenie bezpieczenstwa.