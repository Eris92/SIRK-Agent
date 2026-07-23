# Architektura

## Cel

MeshCentral Workspace dodaje alternatywne srodowisko zdalnej administracji bez modyfikowania core MeshCentral ani oryginalnego modulu Desktop.

Administrator moze pracowac w:

- widocznej sesji uzytkownika,
- izolowanym Workspace A,
- izolowanym Workspace B.

## Glowne komponenty

### MeshCentral-Plugin

Odpowiada za:

- zakladke `Pulpit -New`,
- model sesji i slotow,
- kontrole praw do wezla,
- wysylanie polecen przez MeshAgent,
- odbieranie wynikow i prezentacje statusu,
- moduly `virtualmedia.js` oraz `appcontrol.js`.

### MeshAgent

Jest transportem pomiedzy serwerem MeshCentral a hostem Windows. Obecnie uruchamia polecenia PowerShell jako SYSTEM, wdraza `WorkspaceHost.exe`, rozpoczyna i zatrzymuje sesje oraz zwraca wyniki operacji.

### WorkspaceHost.exe

Natywny proces Windows C++ uruchamiany poczatkowo przez SYSTEM. Host:

1. znajduje aktywna sesje interaktywna,
2. pobiera token uzytkownika,
3. uruchamia worker w tej sesji,
4. tworzy lub wybiera docelowy desktop,
5. wystawia heartbeat przez Named Pipe.

Dla slotow administracyjnych tworzone sa desktopy:

```text
winsta0\SirK-Admin-1
winsta0\SirK-Admin-2
```

### UI przegladarki

UI wyswietla karty slotow, diagnostyke, liste okien, launcher aplikacji oraz wybor trybu urzadzen.

## Aktualny przeplyw startu

```text
Browser
  -> POST pluginadmin.ashx?pin=workspace&asset=start
  -> module.js
  -> kontrola praw MeshCentral
  -> MeshAgent runcommands jako SYSTEM
  -> pobranie WorkspaceHost.exe z develop-latest
  -> weryfikacja SHA-256 i wersji
  -> start bootstrapu
  -> WTSQueryUserToken / CreateProcessAsUser
  -> worker w sesji uzytkownika
  -> Named Pipe heartbeat
  -> wynik przez MeshAgent
  -> stan sesji w pluginie
  -> UI
```

## Sloty i izolacja

```text
user   -> winsta0\default
admin1 -> winsta0\SirK-Admin-1
admin2 -> winsta0\SirK-Admin-2
```

Kazdy slot ma osobny rekord sesji, wlasciciela, PID, Session ID, desktop, stan i diagnostyke. Workspace administracyjny nie moze byc przelaczany na ekran zwyklego uzytkownika.

## Aplikacje i okna

`appcontrol.js` wykonuje dwie operacje:

- enumeracja widocznych okien na konkretnym HDESK,
- uruchomienie procesu z `STARTUPINFO.lpDesktop` ustawionym na docelowy desktop.

Pierwsza wersja korzysta z kodu C# kompilowanego przez PowerShell `Add-Type`. Docelowo krytyczne operacje powinny zostac przeniesione do stalego protokolu WorkspaceHost, aby uniknac wielokrotnej kompilacji i uproscic transport.

## Virtual Media

Aktualny prototyp:

```text
HTTPS URL
  -> zdalny host pobiera ISO
  -> C:\ProgramData\SirK\Workspace\Media
  -> Mount-DiskImage
  -> Windows widzi naped DVD
```

Docelowy przeplyw:

```text
Przegladarka
  -> strumieniowy upload do biblioteki ISO na serwerze MeshCentral
  -> SHA-256 i metadane
  -> wybor obrazu z biblioteki
  -> kontrolowany transfer do hosta lub strumieniowy backend
  -> wirtualny naped
```

Duzych obrazow nie wolno przesylac jako Base64 ani buforowac w calosci w pamieci Node.js.

## Urzadzenia

### Device Broker

Warstwa protokolowa dla urzadzen, ktorych nie trzeba przekazywac jako surowe USB:

- PIV / Smart Card przez WinSCard i APDU,
- audio,
- kamera,
- przyszle wyspecjalizowane integracje.

### USB Passthrough

Osobny tryb dla urzadzen wymagajacych pelnej obecnosci USB, np. programatory, dongle, adaptery i wybrane nosniki.

To samo urzadzenie nie moze byc jednoczesnie przypisane do brokera i passthrough.

### PIV

Docelowy przeplyw:

```text
YubiKey na komputerze administratora
  -> lokalny Workspace PIV Client
  -> WinSCard/APDU przez szyfrowany kanal
  -> zdalny Smart Card Broker
  -> CAPI/CNG i aplikacje Windows
```

PIN powinien byc obslugiwany lokalnie i nie moze byc zapisywany na serwerze ani w logach.

## Planowany capture i input

```text
Workspace desktop
  -> DXGI capture
  -> encoder
  -> kanal MeshCentral
  -> browser canvas

Browser events
  -> kanal sesji
  -> WorkspaceHost
  -> input skierowany do wybranego desktopu
```

Capture, input i clipboard musza byc powiazane z konkretnym slotem i wlascicielem sesji.

## Zasady bezpieczenstwa

- Nie modyfikuj core MeshCentral.
- Kazda operacja wymaga uwierzytelnionego uzytkownika i praw do wezla.
- Host GUI pracuje w sesji interaktywnej, nie jako SYSTEM.
- Polecenia SYSTEM musza byc ograniczone do wdrozenia, bootstrapu i operacji wymagajacych podniesionych praw.
- Nie zapisuj hasel, tokenow, PIN-ow, sekretow ani clipboardu w logach.
- Waliduj sciezki, adresy, identyfikatory sesji i dane zwracane przez hosta.
- Wszystkie operacje asynchroniczne musza miec timeout i jawny stan bledu.
