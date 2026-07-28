# Browser Bridge

Browser Bridge składa się z rozszerzenia Manifest V3 dla Edge/Chrome,
`SirkAgent.BrowserHost.exe` oraz chronionego named pipe usługi.

Rozszerzenie raportuje:

- aktywną kartę, domenę i URL;
- nawigację;
- pobrania;
- wybór plików i drag-and-drop;
- wysłanie formularza;
- wynik żądań upload `POST`, `PUT` i `PATCH`.

Nie odczytuje treści stron, treści pól formularzy, haseł ani zawartości plików.

## Autoryzacja

Usługa przyjmuje zdarzenie tylko gdy:

1. SID klienta named pipe jest SID-em aktywnej sesji konsoli;
2. podpisana polityka ma tryb `Investigation` lub `InsiderRisk`;
3. polityka ma `Case ID` i nie wygasła;
4. typ zdarzenia i domena znajdują się na allowliście.

Przykładowy zakres:

```json
{
  "browserBridge": {
    "enabled": true,
    "allowedDomains": ["example.com"],
    "allowedEvents": [
      "tab",
      "navigation",
      "download",
      "uploadSelection",
      "dragDrop",
      "formSubmit",
      "uploadResult"
    ]
  }
}
```

Subdomeny dozwolonej domeny są akceptowane. Inne schematy niż HTTP/HTTPS,
nieznane typy i komunikaty większe niż 256 KiB są odrzucane.

## Instalacja

Pakiet zawiera katalog `BrowserExtension` i rejestruje native messaging host
`pl.sirk.agent.browser` dla Edge i Chrome. Stały identyfikator rozszerzenia:

```text
kmjplemahkjpfoephgcalhmipelkaion
```

Rozszerzenie należy wdrożyć polityką przedsiębiorstwa albo załadować z katalogu
`C:\Program Files\SIRK Agent\BrowserExtension` na stanowisku testowym.
