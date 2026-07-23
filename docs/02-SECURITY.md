# Bezpieczenstwo SIRK Management Platform

## Model

SMP stosuje Zero Trust. Sam fakt dostarczenia polecenia przez MeshCentral, portal lub lokalny transport nie oznacza zaufania.

Kazde polecenie musi zawierac co najmniej:

- wersje protokolu,
- jednoznaczny `requestId`,
- typ akcji,
- identyfikator urzadzenia i operatora,
- czas wystawienia i wygasniecia,
- nonce albo inny mechanizm anty-replay,
- informacje autoryzacyjne,
- podpis albo uwierzytelniona sesje transportowa.

## Zasady wykonania

- Brak `Invoke-Expression`, `DownloadString`, `EncodedCommand` i dynamicznego kodu PowerShell.
- Pliki wykonywalne i skrypty musza byc dostarczane w kontrolowanym pakiecie, wersjonowane i weryfikowane SHA-256.
- Docelowo binaria oraz skrypty administracyjne musza byc podpisane Authenticode.
- Parametry musza byc przekazywane jako dane, nigdy przez doklejanie kodu do komendy.
- Operacje SYSTEM, terminal, kamera, mikrofon, input i pliki wymagaja osobnych polityk.
- Zwykly uzytkownik nie moze modyfikowac katalogu runtime ani konfiguracji maszyny.

## Tozsamosc urzadzenia

Agent generuje unikalna tozsamosc urzadzenia przy enrollment. Token instalacyjny jest jednorazowy i nie moze pozostawac jako staly sekret. Docelowo klucz prywatny powinien byc chroniony przez DPAPI Machine albo TPM.

## Aktualizacje

Kazda aktualizacja:

1. pobiera manifest,
2. weryfikuje podpis i hash,
3. instaluje obok aktywnej wersji,
4. wykonuje test zdrowia,
5. przeprowadza atomowe przelaczenie,
6. automatycznie wraca do poprzedniej wersji po bledzie.

Agent nie moze nadpisywac samego siebie bez mechanizmu updatera i rollbacku.

## Audyt

Log audytowy musi wskazywac kto, kiedy, z jakiego kanalu i na jakim urzadzeniu wykonal operacje. Sekrety, tokeny i pelna zawartosc poufnych danych nie moga trafac do logow.
