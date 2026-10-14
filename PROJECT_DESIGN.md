# Dokumentacja Projektowa: WhiteList API (Azure Container Apps)

## 1. Architektura Rozwiązania
Aplikacja wystawia endpointy zgodne z API Wykazu Podatników VAT (Biała Lista) Ministerstwa Finansów:
- `GET /k/{apiKey}/api/check/nip/{nip}/bank-account/{bankAccount}?date=YYYY-MM-DD`
- `GET /k/{apiKey}/api/check/regon/{regon}/bank-account/{bankAccount}?date=YYYY-MM-DD`
- `GET /api/check/nip/{nip}/bank-account/{bankAccount}?date=YYYY-MM-DD`
- `GET /api/check/regon/{regon}/bank-account/{bankAccount}?date=YYYY-MM-DD`
- `GET /healthz`

## 2. Przepływ danych (Data Pipeline)
1. **Synchronizacja w tle (`WhiteListSyncBackgroundService`):**
   - Utrzymuje lokalnie pliki dla 3 ostatnich dni (`Europe/Warsaw`).
   - Przy starcie sprawdza obecność gotowych plików `{RRRRMMDD}.bin` (ok. 80 MB) i `{RRRRMMDD}.meta.json` w **Azure Blob Storage**.
   - Jeśli plików nie ma w Blob Storage, pobiera archiwum `{RRRRMMDD}.7z` (~240 MB) z serwerów MF, rozpakowuje je w locie (`SharpCompress`) i strumieniowo parsuje przez `Utf8JsonReader` (`FlatFileStreamParser`), zamieniając 128-znakowe skróty hex na posortowaną tablicę 16-bajtowych wartości `UInt128`.
   - Gotowe pliki `.bin` i `.meta.json` są wysyłane do Azure Blob Storage (skracając kolejne zimne starty kontenera do ~1 sekundy).
2. **Obsługa żądania HTTP:**
   - **Rate Limiting:** Globalny `ConcurrencyLimiter` (max 16 równoległych żądań, `QueueLimit = 0`) + dzienny `FixedWindowRateLimiter` per klucz klienta wyciągnięty ze ścieżki URL (`/k/{apiKey}/...`), query string (`?apiKey=...`) lub adresu IP.
   - **Pre-Flight Check:** Walidacja sumy kontrolnej NIP / REGON oraz rachunku NRB (Modulo 97) przed uruchomieniem funkcji skrótu (poniżej 1 µs).
   - **Weryfikacja w pliku lokalnym (poniżej 1 ms):**
     - Wyliczenie 5000 iteracji SHA-512 na stosie (`stackalloc`, 0 B alokacji GC).
     - Wyszukanie binarne (`BinarySearch`) w zmapowanym pliku `MemoryMappedFile` (max 23 porównania dla 5 mln rekordów).
     - W przypadku braku trafienia: sprawdzenie masek rachunków wirtualnych pogrupowanych po 4-cyfrowym kodzie banku (`FrozenDictionary<uint, string[]>`).
   - **Fallback do API MF + MemoryCache:**
     - Jeśli żądana data wykracza poza 3-dniowe okno retencji lub trwa właśnie pobieranie pliku po północy, zapytanie jest przekazywane do oficjalnego API MF, a wynik trafia do `IMemoryCache`.
   - **Logowanie audytowe (`requestId`):**
     - Każde sprawdzenie generuje unikalny `requestId` i przekazuje go asynchronicznie do `Channel<AuditLogEntry>`, który zapisuje paczki (batch) do **Azure Blob Storage** jako dowód dochowania należytej staranności.
