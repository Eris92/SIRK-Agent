# Evidence Engine

## Cel

Evidence Engine zapewnia wykrywalnosc modyfikacji zdarzen i pozwala przygotowac spojny material dla postepowania wewnetrznego lub analizy incydentu.

## Minimalny format zdarzenia

```json
{
  "eventId": "uuid",
  "timestampUtc": "2026-07-25T06:15:12.428Z",
  "monotonicSequence": 18422,
  "tenantId": "tenant-guid",
  "deviceId": "device-guid",
  "userSid": "S-1-5-21-...",
  "sessionId": 2,
  "caseId": "IR-2026-0041",
  "policyId": "policy-guid",
  "policyVersion": 12,
  "category": "Browser",
  "action": "FileUploadCompleted",
  "data": {},
  "previousEventHash": "base64-sha256",
  "eventHash": "base64-sha256"
}
```

## Lancuch integralnosci

`eventHash` jest liczony z kanonicznej postaci zdarzenia oraz `previousEventHash`. Usuniecie, dopisanie lub zmiana zdarzenia przerywa lancuch.

Zdarzenia sa grupowane w podpisane paczki. Paczka zawiera:

- pierwszy i ostatni numer sekwencji,
- pierwszy i ostatni hash,
- czas utworzenia,
- identyfikator urzadzenia,
- hash zawartosci paczki,
- podpis urzadzenia.

## Czas

Kazde zdarzenie przechowuje:

- czas UTC systemu,
- monotoniczny licznik procesu lub systemu,
- ostatnie znane odchylenie od czasu serwera,
- informacje o zmianie zegara.

Zmiana czasu hosta nie moze pozostac niezauwazona.

## Bufor lokalny

- szyfrowany,
- odporny na czesciowy zapis,
- z limitem rozmiaru,
- z priorytetami zdarzen,
- z kontrola integralnosci rekordow,
- usuwany dopiero po potwierdzeniu odbioru przez serwer.

Przy braku miejsca najpierw usuwane sa niskopriorytetowe dane diagnostyczne. Zdarzenia krytyczne i informacje o luce w sekwencji nie moga byc cicho pomijane.

## Magazyn serwerowy

Docelowy magazyn powinien wspierac:

- append-only lub WORM,
- retencje zalezne od typu sprawy,
- RBAC,
- audit odczytu i eksportu,
- legal hold,
- weryfikacje podpisu i lancucha hashy.

## Eksport sprawy

Eksport zawiera:

- raport czytelny dla analityka,
- surowe zdarzenia,
- manifest plikow i hashy,
- certyfikaty potrzebne do weryfikacji,
- wynik sprawdzenia lancucha,
- informacje o lukach i utracie lacznosci,
- audit utworzenia eksportu.

## MVP

- kanoniczny serializer,
- SHA-256 chain,
- lokalny monotoniczny sequence,
- podpis paczek certyfikatem urzadzenia,
- walidator integralnosci,
- test zmiany, usuniecia i przestawienia zdarzenia.