# Prompt dla Coding Agenta (Kontynuacja prac)

Przeczytaj najpierw pliki `AGENTS.md` oraz `PROJECT_DESIGN.md`, a następnie przeanalizuj `Program.cs`.

## Kontekst
Budujemy produkcyjne API w .NET 10 kompatybilne z endpointem `check` Białej Listy MF, hostowane na Azure Container Apps z cache'owaniem skonwertowanych plików binarnych (`.bin` oraz `.meta.json`) w Azure Blob Storage.

## Zadania do wykonania w tej iteracji:
1. **Podział `Program.cs` na czystą strukturę katalogów:**
   Rozbij monolityczny plik `Program.cs` na moduły z zachowaniem wszystkich optymalizacji `Span<T>`, `stackalloc` i `unsafe`:
   - `Core/WhiteListHasher.cs`
   - `Core/PolishIdentifiersValidator.cs` (walidacja sumy kontrolnej NIP, 9- i 14-cyfrowego numeru REGON oraz NRB Modulo 97)
   - `Storage/WhiteListBinaryStore.cs`
   - `Storage/VirtualAccountMaskMatcher.cs`
   - `Storage/FlatFileStreamParser.cs`
   - `Services/MultiDayWhiteListManager.cs` (z `IMemoryCache` dla zapytań Fallback do MF)
   - `Services/WhiteListSyncBackgroundService.cs`
   - `Services/AuditLogChannelService.cs` (nieblokujący zapis `requestId`, `NIP/REGON`, `NRB`, `Data` i wyniku weryfikacji do Azure Blob/Table Storage przez `System.Threading.Channels.Channel<T>`)
2. **Testy jednostkowe (xUnit):**
   Utwórz projekt testowy `WhiteListApi.Tests` i napisz testy weryfikujące:
   - Zgodność 5000 iteracji `WhiteListHasher.ComputeTruncatedHash` z oficjalnym algorytmem MF.
   - Poprawność dopasowywania masek rachunków wirtualnych (`Y` i `X` w `VirtualAccountMaskMatcher`).
   - Poprawność walidatorów NIP, REGON (9/14) oraz NRB (Modulo 97).
   - Poprawność `BinarySearch` na pliku binarnym `UInt128`.
3. **Konfiguracja kluczy klientów z `appsettings.json` / Azure Environment Variables:**
   Przenieś słownik `apiKeysQuota` z kodu do sekcji konfiguracyjnej `ClientQuotas` ładowanej przez `IConfiguration` (z obsługą łatwej definicji przez zmienne środowiskowe w Azure Container Apps).
