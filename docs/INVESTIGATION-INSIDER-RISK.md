# Investigation i Insider Risk

## Cel

Tryby sluza do czasowego, formalnie zatwierdzonego zbierania rozszerzonej telemetrii dla konkretnej sprawy. Nie sa domyslnym trybem pracy agenta.

## Investigation Mode

Wymagane dane:

- `caseId`,
- powod i kod scenariusza,
- wskazany uzytkownik, urzadzenie lub sesja,
- czas rozpoczecia i zakonczenia,
- role zatwierdzajace,
- jawnie wlaczony zakres kolektorow,
- polityka retencji.

Mozliwy zakres:

- aktywne procesy i okna,
- zmiany fokusu i idle,
- timing myszy i klawiatury,
- metadane schowka,
- aktywnosc plikow i USB,
- kontekst UI Automation,
- domena i URL z Browser Bridge,
- upload i download,
- screenshot na zdarzenie,
- czasowy stream o ograniczonej liczbie klatek.

Domyslnie nie sa zapisywane znaki klawiatury, hasla ani pelna tresc schowka. Rozszerzenie zakresu wymaga osobnej, podpisanej polityki sprawy.

## Departing Employee / Insider Risk

Tryb jest uruchamiany po formalnym triggerze HR lub Security i moze obejmowac okres poprzedzajacy odejscie pracownika.

Najwazniejsze scenariusze:

- masowe pobranie danych,
- kopiowanie na USB,
- archiwizacja i szyfrowanie,
- upload do prywatnej chmury,
- wysylka na prywatna poczte,
- zewnetrzne udostepnienie SharePoint lub OneDrive,
- drukowanie,
- usuwanie sladow po transferze,
- nietypowe zachowanie wzgledem profilu bazowego.

## Korelacja

System nie ocenia pojedynczego eventu w oderwaniu. Tworzy sekwencje:

```text
MassDownload -> ArchiveCreated -> BrowserOpened -> UploadCompleted -> SourceDeleted
```

Kazdy element sekwencji zawiera czas UTC, Device ID, SID uzytkownika, Session ID, proces, plik i hash, kanal docelowy, Policy ID oraz Case ID.

## Eskalacja

Przykladowe progi:

- niski: metadane i alert,
- sredni: screenshot na zdarzenie i zwiekszona czestotliwosc telemetrii,
- wysoki: czasowy rozszerzony capture po podpisanej zmianie polityki,
- krytyczny: blokada DLP realizowana przez zatwierdzony modul ochronny.

## Raport

Raport sprawy powinien zawierac:

- zakres i czas obowiazywania polityki,
- osoby zatwierdzajace,
- os zdarzen,
- powiazane pliki i ich hashe,
- kanal docelowy,
- dowody wizualne, jezeli byly wlaczone,
- manifest integralnosci,
- audit dostepu i eksportu.