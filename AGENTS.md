# AGENTS.md - Wytyczne architektoniczne dla Coding Agenta

## 1. Cel projektu
Wysokowydajne API w .NET 10 (ASP.NET Core Minimal API) kompatybilne 1:1 z endpointami `check` Białej Listy Podatników VAT Ministerstwa Finansów (`/api/check/nip/{nip}/bank-account/{konto}` oraz `/api/check/regon/{regon}/bank-account/{konto}`).
Projekt jest zoptymalizowany pod kątem hostingu w **Azure Container Apps** (plan Consumption: `0.5 vCPU`, `1.0 GiB RAM`, skalowanie `0..2`).

## 2. Niezmienniki architektoniczne (NIE ŁAMAĆ!)
1. **Zero alokacji w gorącej ścieżce (Hot-Path Zero-Alloc):**
   - Wyliczanie 5000 iteracji SHA-512 (`WhiteListHasher.ComputeTruncatedHash`) MUSI odbywać się wyłącznie na stosie (`stackalloc byte[64]`, `stackalloc byte[128]`) przy użyciu `SHA512.HashData`.
   - ZAKAZ używania `Convert.ToHexString()`, `Encoding.UTF8.GetBytes(string)` lub `Linq` wewnątrz pętli hashowania oraz podczas dopasowywania masek (`VirtualAccountMaskMatcher`).
2. **Reprezentacja danych i BinarySearch (`UInt128`):**
   - Skróty SHA-512 z pliku płaskiego MF są obcinane do pierwszych 16 bajtów (128 bitów = 32 znaki hex) i zapisywane jako `UInt128` w porządku **Big-Endian** (`BinaryPrimitives.ReadUInt128BigEndian`), co zachowuje identyczny porządek sortowania jak tekstowy hex.
   - Pliki `.bin` są mapowane z dysku za pomocą `MemoryMappedFile` i przeszukiwane przez `ReadOnlySpan<UInt128>.BinarySearch()`. Nie wprowadzaj bazy SQL/SQLite do odczytu skrótów.
3. **Zarządzanie pamięcią RAM (Hard Limit 512 MB - 1 GiB):**
   - `ServerGarbageCollection` musi pozostać ustawione na `false` (Workstation GC).
   - Parsowanie pliku JSON z archiwum `.7z` (większego niż 1 GB po rozpakowaniu) odbywa się wyłącznie strumieniowo przez `Utf8JsonReader` na buforze 64 KB z `ArrayPool<byte>.Shared`.
4. **Brak nagłówków HTTP od klientów:**
   - Klienci (systemy ERP/FK) nie mogą wysyłać własnych nagłówków HTTP (np. `X-Api-Key`).
   - Identyfikacja klucza klienta dla Rate Limitera odbywa się przez prefiks w ścieżce URL (`/k/{apiKey}/api/check/...`), parametr query (`?apiKey=...`) lub fallback na adres IP.
5. **Niezawodność i Fallback do API MF:**
   - Utrzymujemy retencję lokalnych plików `.bin` dla **3 dni** (dzisiaj, wczoraj, przedwczoraj wg strefy `Europe/Warsaw`).
   - Jeśli klient pyta o datę starszą niż 3 dni, albo plik na bieżący dzień jest właśnie pobierany/przetwarzany w tle, API MUSI automatycznie odpytać oficjalny endpoint MF i zbuforować odpowiedź w `IMemoryCache`.

## 3. Standardy kodowania
- Język: C# 13 / .NET 10, Minimal API.
- Wszelkie operacje I/O (Blob Storage, Table Storage, HTTP) muszą być asynchroniczne i przyjmować `CancellationToken`.
- Logowanie audytowe (`requestId`) nie może blokować wątku odpowiedzi HTTP (używaj `Channel<AuditLogEntry>` w tle).
