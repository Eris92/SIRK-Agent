# SIRK-WorkspaceHost

`SIRK-WorkspaceHost` jest ograniczonym procesem pomocniczym uruchamianym w zweryfikowanej, interaktywnej sesji Windows.

## Odpowiedzialnosc

- sprawdza, czy faktycznie dziala w oczekiwanej sesji,
- odrzuca Session 0,
- laczy sie tylko z jednorazowym Named Pipe przekazanym przez `SIRK-Agent`,
- przedstawia jednorazowy token podczas handshake,
- nie przyjmuje dynamicznego kodu ani polecen PowerShell,
- docelowo udostepni kontrolowane operacje obrazu, inputu i schowka.

## Kontrakt startowy

```text
SIRK-WorkspaceHost.exe \
  --session-id <id> \
  --pipe-name <jednorazowa-nazwa> \
  --token <losowy-base64url>
```

Nazwa pipe nie moze zawierac separatorow sciezki. Token musi miec co najmniej 256 bitow entropii reprezentowanych jako Base64URL.

## Stan

Aktualna wersja implementuje bezpieczny start, walidacje sesji, limitowane ramkowanie IPC i handshake `WorkspaceHost.Hello`. Nie implementuje jeszcze przechwytywania obrazu ani uruchamiania procesu przez `SIRK-Agent`.
