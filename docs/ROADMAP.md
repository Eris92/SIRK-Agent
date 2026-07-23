# Roadmap

## Zrealizowane fundamenty

### Plugin i sesje

- zakladka `Pulpit -New`,
- serwerowy model sesji i slotow,
- kontrola dostepu do wezla,
- start i stop przez MeshAgent,
- automatyczna publikacja galezi `plugin`.

### WorkspaceHost

- natywny host Windows C++,
- statyczny runtime MSVC,
- bootstrap z SYSTEM do aktywnej sesji interaktywnej,
- sloty `user`, `admin1`, `admin2`,
- izolowane desktopy `SirK-Admin-1` i `SirK-Admin-2`,
- heartbeat Named Pipe,
- okno testowe i diagnostyka.

### UI i moduly pomocnicze

- stan procesu, heartbeat, pipe, desktop i GUI,
- dane sesji Windows, PID, rozdzielczosc i monitory,
- wybor Device Broker / USB Passthrough / Virtual Media,
- techniczny prototyp montowania ISO z HTTPS,
- lista okien na wybranym desktopie,
- uruchamianie aplikacji na wybranym desktopie,
- pojedyncza klatka podgladu Workspace,
- enumeracja kamer, mikrofonow i glosnikow hosta.

## 0.9.x - capture obrazu

- stabilizacja pojedynczej klatki testowej,
- inicjalizacja DXGI Desktop Duplication dla aktywnego pulpitu,
- capture konkretnego Workspace,
- obsluga zmiany rozdzielczosci,
- metryki FPS,
- kompresja oraz transport do pluginu,
- canvas w przegladarce,
- reconnect i kontrola limitow przepustowosci.

Uwaga: izolowany desktop moze wymagac innej techniki capture niz aktywny pulpit DXGI. Implementacja musi byc testowana na `default` i ukrytych HDESK.

## 1.0.x - input

- mysz,
- klawiatura,
- mapowanie wspolrzednych,
- klawisze specjalne,
- fokus i aktywacja okna,
- blokada inputu do konkretnego slotu,
- oddzielny kursor operatora.

## 1.1.x - clipboard

- tekst UTF-8,
- pliki jako osobny, kontrolowany transfer,
- wlaczanie i wylaczanie per sesja,
- limity rozmiaru,
- brak logowania zawartosci.

## 1.2.x - biblioteka Virtual Media

- wybor pliku ISO/IMG z komputera administratora,
- strumieniowy upload na serwer MeshCentral,
- biblioteka obrazow z nazwa, rozmiarem i SHA-256,
- uprawnienia do biblioteki,
- wybor obrazu i podlaczenie do hosta,
- progress uploadu oraz transferu,
- odmontowanie,
- usuwanie i limit miejsca,
- opcjonalne automatyczne sprzatanie.

Pole HTTPS pozostanie opcja dodatkowa, a nie glownym sposobem dostarczenia ISO.

## 1.3.x - PIV Smart Card Broker

- lokalny klient administratora,
- wykrywanie czytnikow WinSCard,
- przypisanie karty do jednej sesji Workspace,
- przekazywanie APDU przez bezpieczny kanal,
- lokalna obsluga PIN,
- zdalny logiczny czytnik Smart Card,
- test `certutil -scinfo`,
- test CAPI/CNG i podpisu,
- pozniej test logowania smart card.

## 1.4.x - USB Passthrough

- lokalna lista urzadzen USB,
- zgoda uzytkownika na przekazanie,
- lease urzadzenia dla konkretnego Workspace,
- szyfrowany transport,
- zdalny wirtualny kontroler lub zgodny backend USB/IP,
- bezpieczne odlaczenie i odzyskanie urzadzenia lokalnie,
- whitelist/blacklist klas urzadzen.

## 1.5.x - Media Broker

### Fundament

- enumeracja kamer,
- enumeracja mikrofonow,
- enumeracja glosnikow,
- polityka per host i profil,
- osobne uprawnienie `MediaCapture`,
- audyt kazdej operacji.

### Incident Evidence

- snapshot z kamery,
- ograniczone czasowo nagranie wideo i audio,
- screenshot lub nagranie pulpitu,
- logi, procesy, sesje i polaczenia,
- wymagany identyfikator incydentu i powod,
- manifest, SHA-256 i szyfrowany pakiet ZIP,
- retencja i automatyczne sprzatanie.

### Home Monitor

- podglad kamery i audio przez WebRTC,
- rozmowa dwukierunkowa,
- snapshot i nagrywanie,
- wykrywanie ruchu,
- powiadomienia,
- jawne przypisanie profilu do prywatnego urzadzenia.

Capture, nagrywanie i streaming pozostaja domyslnie wylaczone. Sprzetowa sygnalizacja kamery i mikrofonu nie moze byc obchodzona.

Szczegoly: `docs/MEDIA-BROKER.md`.

## 1.6.x - wirtualny display

- sterownik wirtualnego monitora,
- niezalezna rozdzielczosc Workspace,
- wielu operatorow i monitorow,
- obsluga skalowania DPI,
- podpisywanie i instalacja sterownika.

## Kryteria wersji stabilnej

- brak modyfikacji core MeshCentral,
- aktualizacja pluginu nie psuje oryginalnego Desktop,
- izolacja slotow potwierdzona testami,
- kontrola praw dla kazdej operacji,
- timeout i anulowanie operacji,
- bezpieczne reconnect,
- brak sekretow w logach,
- poprawne sprzatanie procesow, pipe, desktopow, obrazow i urzadzen,
- testy na Windows 10/11 i Windows Server.