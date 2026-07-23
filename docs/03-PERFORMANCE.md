# Wydajnosc i slabe lacza

SMP ma byc projektowana dla laczy wolnych, niestabilnych i o wysokim RTT. Szybkie lacze nie moze byc wymaganiem poprawnego dzialania.

## Zasady

- staly proces agenta zamiast uruchamiania PowerShell dla kazdej operacji,
- polaczenia dlugotrwale i szybki reconnect,
- wysylanie zmian zamiast pelnych klatek,
- adaptacyjny bitrate, FPS, rozdzielczosc i jakosc,
- priorytetyzacja inputu i sterowania nad obrazem,
- ograniczanie liczby kopii bufora,
- kompresja i batching tylko wtedy, gdy zmniejszaja calkowita latencje,
- kontrola backpressure i limitow kolejek.

## Tryby pulpitu

1. Bootstrap: pojedyncza klatka do diagnostyki.
2. Low bandwidth: dirty rectangles, niskie FPS, silna kompresja.
3. Interactive: adaptacyjny strumien o niskiej latencji.
4. Quality: wyzsza jakosc, gdy lacze i sprzet pozwalaja.

JPEG/PNG nie sa docelowym formatem streamingu. Docelowy pipeline powinien wspierac kodek wideo, adaptacje oraz aktualizacje regionow.

## Telemetria techniczna

Agent i klient mierza RTT, jitter, utrate pakietow, bitrate, FPS, czas capture, encode, transport i decode, CPU, GPU oraz dlugosc kolejek. Dane steruja adaptacja polaczenia i nie moga zawierac tresci uzytkownika.

## Cele poczatkowe

- pierwsza odpowiedz sterujaca bez uruchamiania nowego interpretera,
- reconnect bez ponownej instalacji i bez utraty tozsamosci,
- uzywalne sterowanie przy okolo 512 kbps,
- degradacja jakosci obrazu zamiast blokowania inputu,
- brak nieograniczonych buforow i kolejek.
