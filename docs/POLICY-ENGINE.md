# Policy Engine

## Cel

Policy Engine jest jedynym mechanizmem zmiany zachowania SIRK Agent. Agent nie honoruje lokalnych plikow konfiguracyjnych, wpisow rejestru ani parametrow uruchomieniowych zmieniajacych polityke bezpieczenstwa.

## Model zaufania

```text
SIRK Management Server
        |
        | podpisana paczka polityki
        v
Policy Receiver -> Signature Verifier -> Scope Validator -> Replay Guard
        |                                      |
        +-----------------> Policy Store <-----+
                               |
                               v
                         Runtime Policy
```

## Wymagane pola paczki

```json
{
  "schemaVersion": 1,
  "tenantId": "tenant-guid",
  "deviceId": "device-guid",
  "policyId": "policy-guid",
  "policyVersion": 12,
  "epoch": 4,
  "sequence": 184,
  "issuedAtUtc": "2026-07-25T06:00:00Z",
  "notBeforeUtc": "2026-07-25T06:00:00Z",
  "expiresAtUtc": "2026-07-25T18:00:00Z",
  "mode": "Investigation",
  "caseId": "IR-2026-0041",
  "nonce": "base64-random-value",
  "payload": {},
  "signature": {
    "algorithm": "ES256",
    "keyId": "policy-signing-2026-01",
    "value": "base64-signature"
  }
}
```

## Kolejnosc walidacji

1. poprawny format i wspierana wersja schematu,
2. znany `keyId` i dozwolony algorytm,
3. poprawny podpis nad kanoniczna reprezentacja paczki bez pola `signature.value`,
4. zgodny `tenantId`,
5. zgodny `deviceId` albo jawnie podpisana polityka grupowa,
6. aktualny czas miesci sie w `notBeforeUtc` i `expiresAtUtc`,
7. `epoch` nie jest nizszy od zapisanego,
8. `sequence` jest wyzszy od ostatnio zaakceptowanego dla danego epoch,
9. `nonce` nie byl wczesniej uzyty,
10. tryb wymagajacy dochodzenia ma `caseId` i wymagane zatwierdzenia.

## Ochrona przed rollbackiem

Agent zapisuje najwyzsza zaakceptowana pare:

```text
(epoch, sequence)
```

Zwykla polityka nie moze obnizyc zadnej z tych wartosci. Awaryjny rollback wymaga osobnego typu polityki, osobnego klucza podpisujacego i jawnego pola `rollbackTo`.

## Przechowywanie

Lokalnie sa przechowywane:

- aktywna podpisana paczka,
- poprzednia poprawna paczka,
- hash obu paczek,
- najwyzszy `epoch` i `sequence`,
- ograniczony rejestr nonce,
- czas ostatniej udanej synchronizacji.

Cache musi byc szyfrowany i powiazany z tozsamoscia urzadzenia. Lokalna edycja cache powoduje odrzucenie i alert integralnosci.

## Tryby

- `Normal` - standardowa telemetria zdrowia i bezpieczenstwa,
- `Security` - rozszerzona telemetria procesow, plikow i urzadzen,
- `Investigation` - czasowa telemetria dochodzeniowa,
- `InsiderRisk` - korelacja kanalow eksfiltracji,
- `Emergency` - podpisana polityka reakcji awaryjnej.

## Zachowanie offline

- aktywne blokady pozostaja wlaczone,
- polityka czasowa dziala do `expiresAtUtc`,
- po wygasnieciu agent wraca do bezpiecznego trybu bazowego,
- zdarzenia sa buforowane lokalnie,
- brak heartbeat jest raportowany po odzyskaniu lacznosci,
- agent nigdy nie przechodzi do lokalnej konfiguracji zastepczej.

## MVP

Pierwsza implementacja ma zapewnic:

- model paczki,
- kanonizacje JSON,
- weryfikacje ES256,
- scope validation,
- date validation,
- replay/rollback guard,
- atomowe zapisanie zaakceptowanej polityki,
- jednoznaczne kody odrzucenia,
- testy jednostkowe dla kazdego warunku.