# AGENTS.md

## Jezyk i sposob pracy

- Komunikuj sie z uzytkownikiem po polsku.
- Kod, nazwy API i standardowa terminologie techniczna zapisuj zgodnie z konwencja projektu.
- W skryptach PowerShell i komunikatach technicznych preferuj ASCII, chyba ze istniejacy plik uzywa polskich znakow.
- Wprowadzaj kompletne, dzialajace zmiany w repozytorium. Nie zostawiaj pseudokodu jako gotowej implementacji.
- Przed zmiana przeczytaj powiazane pliki i sprawdz aktualny przeplyw danych od UI do MeshAgenta i hosta Windows.

## Cel projektu

MeshCentral Workspace jest osobna wtyczka i procesem Windows, ktore dodaja alternatywny modul `Pulpit -New` bez modyfikowania oryginalnego modulu Desktop MeshCentral.

Docelowa architektura:

```text
MeshCentral Plugin
        -> MeshAgent
        -> WorkspaceHost.exe
        -> capture / input / clipboard / devices
        -> izolowany lub widoczny desktop Windows
```

Projekt ma zapewnic administratorowi osobne Workspace na tym samym hoście, z mozliwoscia rozbudowy o:

- zdalny obraz i sterowanie,
- uruchamianie aplikacji,
- schowek,
- Device Broker,
- PIV / Smart Card Broker,
- USB Passthrough,
- Virtual Media,
- wirtualny ekran i niezalezny kursor.

## Aktualny stan

Wersja pluginu: `0.8.9`.
Wersja `WorkspaceHost.exe`: `0.7.0`.

Dziala:

- uruchamianie hosta przez MeshAgent,
- bootstrap z SYSTEM do aktywnej sesji uzytkownika,
- trzy sloty: `user`, `admin1`, `admin2`,
- izolowane desktopy `SirK-Admin-1` i `SirK-Admin-2`,
- heartbeat przez Named Pipe,
- diagnostyka procesu, desktopu, sesji, rozdzielczosci i okna testowego,
- UI wyboru Device Broker / USB Passthrough / Virtual Media,
- Virtual Media z adresu HTTPS jako pierwsza wersja techniczna,
- lista okien i uruchamianie aplikacji na wybranym desktopie.

Jeszcze nie dziala produkcyjnie:

- DXGI capture i streaming obrazu,
- klawiatura i mysz,
- clipboard,
- biblioteka ISO i upload z przegladarki,
- PIV Smart Card Broker,
- USB Passthrough,
- wirtualny display driver.

## Struktura repozytorium

```text
MeshCentral-Plugin/
  workspace.js       entrypoint pluginu
  module.js          sesje Workspace i komunikacja z MeshAgent
  virtualmedia.js    montowanie i odmontowanie ISO
  appcontrol.js      lista okien i uruchamianie aplikacji
  public/            UI przegladarki

WorkspaceHost/
  src/main.cpp       natywny host Windows
  CMakeLists.txt

WorkspaceCommon/     wspolne modele i pozostalosci pierwszego prototypu
docs/                dokumentacja projektu
tools/               build i testy
.github/workflows/   CI oraz publikacja
```

## Zasady architektury

1. Nie modyfikuj core MeshCentral ani oryginalnego modulu Desktop.
2. Operacje na hoście musza przechodzic przez sprawdzenie praw MeshCentral.
3. Workspace administracyjne musza pozostac niewidoczne dla zwyklego uzytkownika.
4. Kod uruchamiany jako SYSTEM ma tylko wykonac bootstrap lub operacje wymagajace uprawnien. GUI powinno dzialac w docelowej sesji interaktywnej.
5. Nie zapisuj hasel, PIN-ow PIV, tokenow, zawartosci clipboardu ani sekretow w logach.
6. PIV realizuj przez broker WinSCard/APDU, nie jako domyslny raw USB passthrough.
7. USB Passthrough i Device Broker sa osobnymi trybami. To samo urzadzenie nie moze byc aktywne w obu trybach jednoczesnie.
8. Virtual Media ma docelowo korzystac z biblioteki ISO na serwerze MeshCentral i uploadu strumieniowego. Nie przesylaj duzych ISO jako Base64 ani przez pamiec procesu Node.js.
9. Kazda operacja asynchroniczna musi miec stan, timeout, blad i mozliwosc bezpiecznego ponowienia.
10. Nie oznaczaj funkcji jako dzialajacej, dopoki transport i operacja na hoście nie zostaly zaimplementowane i zweryfikowane.

## Wersjonowanie i publikacja

- `develop` jest galezia rozwojowa.
- `plugin` jest galezia dystrybucyjna generowana z `MeshCentral-Plugin/`.
- Zmiana funkcjonalna pluginu wymaga aktualizacji:
  - `MeshCentral-Plugin/config.json`,
  - `MeshCentral-Plugin/package.json`,
  - numeru cache-bustera i komunikatu startowego w `workspace.js`,
  - changelogu lub dokumentacji, gdy zmiana jest widoczna dla uzytkownika.
- Nie edytuj galezi `plugin` recznie jako podstawowego sposobu publikacji. Najpierw napraw workflow na `develop`.
- Po publikacji sprawdz, czy `plugin/config.json` i wymagane pliki frontendu rzeczywiscie odpowiadaja nowej wersji.

## Build i testy

Podstawowy build:

```powershell
.\tools\build.ps1 -Configuration Release -Runtime win-x64
```

Przed uznaniem zmiany za gotowa:

1. sprawdz skladnie JavaScript i PowerShell,
2. zbuduj `WorkspaceHost.exe`, jesli zmieniono C++,
3. uruchom test heartbeat,
4. sprawdz brak zaleznosci od Visual C++ Redistributable,
5. potwierdz, ze plugin ZIP zawiera wszystkie nowe pliki,
6. zweryfikuj publikacje galezi `plugin`,
7. podaj uzytkownikowi konkretny scenariusz testowy.

## Priorytety rozwoju

Najblizsza kolejnosc:

1. ustabilizowanie listy okien i uruchamiania aplikacji,
2. capture obrazu Workspace,
3. streaming do przegladarki,
4. input myszy i klawiatury,
5. clipboard,
6. biblioteka i upload ISO,
7. PIV Smart Card Broker,
8. USB Passthrough,
9. wirtualny display.
