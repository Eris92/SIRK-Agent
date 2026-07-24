# SIRK Protocol - fundament

SIRK Protocol jest wersjonowanym kontraktem pomiedzy portalem, serwerem, adapterami i agentem. Transport moze sie zmieniac, ale znaczenie komunikatow pozostaje stale.

## Minimalna koperta polecenia

```json
{
  "protocolVersion": 1,
  "messageType": "Workspace.CaptureFrame",
  "requestId": "uuid",
  "deviceId": "stable-device-id",
  "operatorId": "operator-id",
  "issuedAt": "2026-07-23T00:00:00Z",
  "expiresAt": "2026-07-23T00:00:15Z",
  "nonce": "unique-value",
  "payload": {},
  "authorization": {},
  "signature": "base64"
}
```

## Minimalna odpowiedz

```json
{
  "protocolVersion": 1,
  "messageType": "Workspace.CaptureFrame.Result",
  "requestId": "uuid",
  "ok": true,
  "error": null,
  "metrics": {
    "durationMs": 0
  },
  "payload": {}
}
```

## Reguly

- nieznane polecenie jest odrzucane,
- wygasle polecenie jest odrzucane,
- ponowne uzycie nonce jest odrzucane,
- payload jest walidowany przed wykonaniem,
- bledy nie ujawniaja sekretow ani wrazliwych szczegolow systemu,
- duze dane binarne nie sa kodowane bez potrzeby w base64; maja uzywac osobnego kanalu strumieniowego,
- kompatybilnosc jest negocjowana przez wersje i capability agenta.

## Kanaly

- Control channel - male polecenia i odpowiedzi o wysokim priorytecie.
- Stream channel - obraz, audio i duze transfery.
- Event channel - heartbeat, status i zdarzenia.
- Update channel - manifesty oraz pakiety aktualizacji.

MeshAdapter i transport standalone musza mapowac dane do tego samego modelu wewnetrznego.
