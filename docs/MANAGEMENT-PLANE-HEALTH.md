# Management Plane Health

## Cel

Agent ocenia nie tylko własny proces, ale także stan systemów zarządzających,
od których zależy bezpieczeństwo urządzenia. Wynik jest zapisywany w:

```text
C:\ProgramData\SIRK\Agent\management-plane-health.json
```

oraz trafia do chronionej Telemetry Queue i Evidence Chain.

## Klasyfikacja hosta

- `Workgroup` — brak członkostwa AD i Microsoft Entra join;
- `ActiveDirectory` — `DomainJoined=YES`;
- `MicrosoftEntra` — `AzureAdJoined=YES`;
- `Hybrid` — jednocześnie `DomainJoined=YES` i `AzureAdJoined=YES`.

Klasyfikacja lokalna korzysta z `dsregcmd /status`. Dla AD Agent sprawdza
secure channel przez `nltest` oraz komputerowy RSoP przez `gpresult`.

## Wymagania podpisanej polityki

Opcjonalna sekcja `settings.managementPlane` zaakceptowanej polityki:

```json
{
  "requiredAppliedGpos": ["SIRK Security Baseline"],
  "requireDefender": true,
  "requireFirewall": true,
  "requireBitLocker": true,
  "requireSecureBoot": true,
  "requireTpm": true,
  "allowSafeRepair": false,
  "entraPolicySnapshot": {
    "retrievedAtUtc": "2026-07-28T12:00:00Z",
    "requiredPolicies": [
      {
        "id": "policy-id",
        "displayName": "Require MFA for administrators",
        "enabled": true,
        "present": true
      }
    ]
  }
}
```

Brak sekcji nie wyłącza bazowych kontroli Defender, Firewall, BitLocker,
Secure Boot i TPM. Oczekiwane GPO oraz snapshot Entra muszą pochodzić z
podpisanej polityki.

## Remediacja

Agent nie wykonuje ryzykownych zmian w ciemno:

- reset secure channel jest możliwy tylko przy `allowSafeRepair=true`;
- BitLocker nie jest automatycznie włączany bez potwierdzonego escrow klucza;
- GPO jest diagnozowane przez RSoP, a naprawa opisuje filtrację, linkowanie,
  replikację i `gpupdate`;
- Conditional Access jest oceniany ze snapshotu Portalu. Zmiany wymagają
  osobnego, zatwierdzonego przepływu z uprawnieniem zapisu.

Każda próba naprawy zapisuje `repairAttempted` i `repairSucceeded`.
