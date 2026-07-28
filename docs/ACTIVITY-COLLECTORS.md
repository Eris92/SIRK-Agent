# Activity Collectors

Kolektory są domyślnie wyłączone. Zakres pochodzi wyłącznie z zaakceptowanej,
podpisanej polityki w `settings.activityCollection`.

```json
{
  "enabled": true,
  "processes": true,
  "interactiveContext": true,
  "clipboardMetadata": true,
  "usb": true,
  "printing": true,
  "fileRoots": ["C:\\CaseData"],
  "intervalSeconds": 300
}
```

Procesy i urządzenia USB mogą być zbierane po jawnym włączeniu. Kontekst sesji,
drukowanie oraz pliki wymagają dodatkowo:

- trybu `Investigation` albo `InsiderRisk`;
- niepustego `Case ID`;
- niewygasłej polityki.

Agent nie zapisuje znaków klawiatury ani treści schowka. Dla schowka zapisywane
są wyłącznie formaty, długość tekstu i liczba plików. Zakres plików jest
ograniczony do jawnych katalogów z polityki, maksymalnie 20 korzeni i 2000
rekordów na korzeń.

Najnowszy wynik:

```text
C:\ProgramData\SIRK\Agent\activity-latest.json
```

Każdy snapshot jest także zapisywany w Evidence Chain oraz chronionej
Telemetry Queue.
