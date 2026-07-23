# SIRK-MeshAdapter

`SIRK-MeshAdapter` jest przejsciowym adapterem pomiedzy MeshCentral/MeshAgent a `SIRK-Agent`.

Adapter nie wykonuje operacji administracyjnych. Jego odpowiedzialnosc jest ograniczona do:

1. odebrania kontrolowanego polecenia z warstwy MeshCentral,
2. walidacji formatu wejscia,
3. utworzenia koperty SIRK Protocol v1,
4. przeslania jej przez lokalny Named Pipe do `SIRK-Agent`,
5. zwrocenia odpowiedzi bez modyfikowania logiki biznesowej.

## Zasady bezpieczenstwa

- brak `Invoke-Expression`, `cmd /c` i dynamicznego PowerShell,
- brak wykonywania dowolnych sciezek lub polecen systemowych,
- dozwolone sa tylko jawnie wymienione `messageType`,
- limity rozmiaru wejscia i odpowiedzi,
- krotki TTL i kryptograficzny nonce,
- jeden komunikat JSON na stdin i jedna odpowiedz JSON na stdout,
- logi diagnostyczne trafiaja na stderr i nie zawieraja payloadu.

## Pierwszy zakres

Adapter obsluguje tylko:

- `System.Ping`,
- `System.GetStatus`,
- `System.GetCapabilities`.

Przyklad:

```powershell
'{"messageType":"System.Ping","deviceId":"PC-001","operatorId":"mesh:admin","payload":{}}' |
    .\SIRK-MeshAdapter.exe
```
