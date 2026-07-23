# Build i publikacja

## Wymagania

- Windows 10/11 lub Windows Server,
- PowerShell 7 albo Windows PowerShell 5.1,
- CMake 3.24 lub nowszy,
- Visual Studio 2022 albo Build Tools 2022,
- workload C++ Desktop Development,
- Git, jesli build jest wykonywany lokalnie z repozytorium.

`WorkspaceHost.exe` jest obecnie natywnym programem C++ i nie wymaga .NET Runtime.

## Build lokalny

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\build.ps1 -Configuration Release -Runtime win-x64
```

Skrypt:

1. odczytuje wersje z `MeshCentral-Plugin\config.json`,
2. konfiguruje projekt CMake,
3. buduje `WorkspaceHost.exe`,
4. kopiuje runtime do `artifacts`,
5. pakuje zawartosc `MeshCentral-Plugin` do ZIP.

Wyniki:

```text
artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe
artifacts\MeshCentral-Workspace-Plugin-<wersja>.zip
```

## Test hosta

Wersja:

```powershell
.\artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe --version
```

Test heartbeat wykonuje skrypt:

```powershell
.\tools\test-workspacehost.ps1 -ExePath .\artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe
```

Host zapisuje log techniczny w:

```text
C:\ProgramData\SirK\Workspace\Logs\workspace.log
```

Log nie moze zawierac hasel, tokenow, PIN-ow PIV, sekretow ani zawartosci clipboardu.

## Runtime MSVC

Release korzysta z `/MT`, a Debug z `/MTd`. Gotowy EXE nie powinien wymagac:

```text
VCRUNTIME*.dll
MSVCP*.dll
CONCRT*.dll
```

GitHub Actions sprawdza zaleznosci przez `dumpbin /dependents`.

## GitHub Actions

Workflow `Build and publish plugin` uruchamia sie po pushu na `develop` i wykonuje:

1. odczyt wersji pluginu,
2. build natywnego hosta,
3. test zaleznosci runtime,
4. test heartbeat,
5. utworzenie ZIP pluginu i runtime,
6. publikacje artefaktow workflow,
7. publikacje prerelease `develop-latest`,
8. force-push zawartosci `MeshCentral-Plugin/` na galaz `plugin`.

## Wersjonowanie

Przy zmianie widocznej dla uzytkownika zaktualizuj spójnie:

```text
MeshCentral-Plugin/config.json
MeshCentral-Plugin/package.json
MeshCentral-Plugin/workspace.js
MeshCentral-Plugin/public/main.js
pozostale pliki frontendu z cache-busterem
```

W `workspace.js` nalezy zaktualizowac:

- komunikat `Plugin <wersja> loaded`,
- cache-buster dla `main.js`, `apps.js` i `main.css`.

Po publikacji sprawdz bezposrednio na galezi `plugin`:

- numer w `config.json`,
- numer w `package.json`,
- obecnosc nowych plikow,
- wpisy `require(...)` w `workspace.js`,
- endpointy GET/POST,
- cache-buster frontendu.

Sama zmiana `config.json` nie oznacza, ze nowy frontend lub backend zostal poprawnie opublikowany.

## Instalacja testowa

MeshCentral pobiera plugin z:

```text
https://codeload.github.com/Eris92/MeshCentral-Workspace/zip/refs/heads/plugin
```

Runtime hosta jest pobierany z prerelease:

```text
develop-latest/WorkspaceHost.exe
develop-latest/WorkspaceHost.exe.sha256
```

Plugin przed wymiana runtime:

1. sprawdza lokalna wersje `WorkspaceHost.exe`,
2. pobiera EXE i plik SHA-256,
3. porownuje hash,
4. sprawdza wersje po instalacji,
5. dopiero potem uruchamia host.

## Minimalna weryfikacja wydania

Po aktualizacji pluginu:

1. zrestartuj usluge MeshCentral,
2. otworz zakladke `Pulpit -New`,
3. uruchom Sesje uzytkownika, Workspace A i Workspace B,
4. sprawdz PID, Session ID, desktop, GUI i heartbeat,
5. uruchom PowerShell w Workspace A,
6. odswiez liste okien,
7. potwierdz, ze okno nie pojawilo sie na pulpicie zwyklego uzytkownika,
8. sprawdz log MeshCentral oraz `workspace.log` pod katem bledow.
