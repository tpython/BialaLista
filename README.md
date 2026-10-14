# Biała Lista Podatników VAT API (.NET 10 / Azure Container Apps)

Wysokowydajny, zoptymalizowany pod kątem minimalnego zużycia pamięci i CPU mikroserwis w **.NET 10 (ASP.NET Core Minimal API)**, kompatybilny 1:1 z oficjalnymi endpointami weryfikacji rachunków bankowych (**metoda `check`**) Wykazu Podatników VAT Ministerstwa Finansów (MF).

Projekt został zaprojektowany z myślą o hostingu w środowisku bezserwerowym **Azure Container Apps** (plan Consumption: `0.5 vCPU`, `1.0 GiB RAM`, autoskalowanie `0..2`) oraz bezpośredniej integracji z systemami księgowo-finansowymi i ERP (np. Comarch, SAP, Enova365, Symfonia).

---

## 1. Kluczowe cechy i niezmienniki architektoniczne

* **Zero alokacji w gorącej ścieżce (Hot-Path Zero-Alloc):**
  Obliczanie 5000 iteracji SHA-512 (`WhiteListHasher.ComputeTruncatedHash`) odbywa się wyłącznie na stosie (`stackalloc byte[64]`, `stackalloc byte[128]`) z użyciem `SHA512.HashData`. Brak alokacji obiektów na stercie GC w trakcie obsługi zapytania weryfikacyjnego.
* **Reprezentacja skrótów i wyszukiwanie binarne (`UInt128`):**
  Skróty SHA-512 z pliku płaskiego MF są obcinane do pierwszych 16 bajtów (128 bitów) i zapisywane jako `UInt128` w formacie **Big-Endian**, co zachowuje identyczny porządek sortowania jak tekst szesnastkowy. Pliki `.bin` są mapowane do pamięci wirtualnej przez `MemoryMappedFile` i przeszukiwane przez `ReadOnlySpan<UInt128>.BinarySearch()` w czasie poniżej 1 µs.
* **Wyszukiwanie masek rachunków wirtualnych:**
  Maski rachunków wirtualnych są grupowane po 4-cyfrowym kodzie rozliczeniowym banku w `FrozenDictionary<uint, string[]>`. Sprawdzane są wyłącznie maski przypisane do danego banku, eliminując zbędne hashowanie.
* **Strumieniowe przetwarzanie pliku płaskiego:**
  Archiwa `.7z` z serwerów MF są rozpakowywane w locie (`SharpCompress 0.48.0`) i parsowane strumieniowo przez `Utf8JsonReader` na buforze 64 KB z `ArrayPool<byte>.Shared`.
* **Retencja 3 dni i Fallback do API MF:**
  Aplikacja utrzymuje retencję lokalnych plików `.bin` dla 3 ostatnich dni wg strefy `Europe/Warsaw`. Jeśli zapytanie dotyczy starszej daty lub plik na bieżący dzień nie został jeszcze opublikowany przez MF po północy, następuje automatyczne odpytanie oficjalnego API MF z buforowaniem w `IMemoryCache` (TTL 6h).
* **Niezależność od nagłówków HTTP:**
  Systemy ERP nie muszą przesyłać własnych nagłówków (np. `X-Api-Key`). Klucz klienta może być przekazany w ścieżce URL (`/k/{apiKey}/api/check/...`), parametrze query (`?apiKey=...`) lub rozliczany po adresie IP klienta (z pełną obsługą `X-Forwarded-For` za Ingress ACA).
* **Asynchroniczny audyt należytej staranności:**
  Każda weryfikacja generuje unikalny `requestId` i trafia do nieblokującego kanału `Channel<AuditLogEntry>`, który zapisuje paczki do **Azure Blob Storage** (`.jsonl`) oraz transakcyjnie do **Azure Table Storage** jako dowód dochowania należytej staranności przed organami skarbowymi.

---

## 2. Diagram Architektury

```mermaid
flowchart TD
    Client["System ERP / Klient HTTP"] -->|"GET /k/{apiKey}/api/check/..."| Ingress["Azure Container Apps Ingress (Envoy)"]
    Ingress -->|"ForwardedHeaders (X-Forwarded-For)"| Kestrel["ASP.NET Core Kestrel (0.5 vCPU)"]

    subgraph Pipeline ["Pipeline Obsługi Zapytania"]
        Kestrel --> Limiter["Rate Limiter (Concurrency max 16 + Dzienny limit per klucz)"]
        Limiter --> PreFlight["Walidacja wstępna (Suma kontrolna NIP, REGON, NRB Modulo 97)"]
        PreFlight --> Manager{"MultiDayWhiteListManager (Data w retencji 3 dni?)"}
        
        Manager -->|Tak: Lokalny plik binarny| Hasher["WhiteListHasher (5000 iteracji SHA-512 na stosie, 0 alokacji)"]
        Hasher --> BinSearch["WhiteListBinaryStore (MemoryMappedFile BinarySearch UInt128)"]
        BinSearch -->|Trafienie bezpośrednie| ResultFound["Wynik: accountAssigned = TAK"]
        BinSearch -->|Brak trafienia| MaskMatch["VirtualAccountMaskMatcher (Maski banku w FrozenDictionary)"]
        MaskMatch --> ResultFinal["Wynik: TAK lub NIE"]

        Manager -->|Nie: Brak pliku lub data > 3 dni| Cache{"IMemoryCache"}
        Cache -->|Hit| CachedResp["Odpowiedź z Cache"]
        Cache -->|Miss| MfFallback["Oficjalna bramka MF (https://wl-api.mf.gov.pl)"]
        MfFallback --> CacheStore["Zapisz w IMemoryCache (TTL 6h)"]
    end

    ResultFound --> Audit["AuditLogChannelService (Channel<AuditLogEntry>)"]
    ResultFinal --> Audit
    CachedResp --> Audit
    CacheStore --> Audit

    subgraph Storage ["Asynchroniczny Zapis Audytu (BackgroundService)"]
        Audit -->|Paczki JSONL| AzureBlob["Azure Blob Storage (audit/RRRRMMDD/GUID.jsonl)"]
        Audit -->|Transakcje wsadowe max 100| AzureTable["Azure Table Storage (whitelist_audit)"]
    end

    subgraph Sync ["Synchronizacja Plików Płaskich w Tle"]
        BlobCheck{"Azure Blob Storage (Istnieje .bin i .meta.json?)"}
        BlobCheck -->|Tak| QuickLoad["Szybki start: pobierz .bin (~80 MB)"]
        BlobCheck -->|Nie| MfDownload["Pobierz .7z z plikplaski.mf.gov.pl (~240 MB)"]
        MfDownload --> StreamParser["FlatFileStreamParser (SharpCompress + Utf8JsonReader)"]
        StreamParser --> UploadBlob["Zapisz .bin i .meta do Azure Blob Storage"]
    end
```

---

## 3. Specyfikacja API

### 3.1. Weryfikacja po numerze NIP

```http
GET /api/check/nip/{nip}/bank-account/{bankAccount}?date=YYYY-MM-DD
GET /k/{apiKey}/api/check/nip/{nip}/bank-account/{bankAccount}?date=YYYY-MM-DD
```

* **Parametry ścieżki:**
  - `apiKey` *(opcjonalny)* – identyfikator klienta zdefiniowany w konfiguracji (`appsettings.json` lub zmienna `CLIENT_QUOTAS`).
  - `nip` – 10-cyfrowy numer NIP (walidowany algorytmem sumy kontrolnej MF).
  - `bankAccount` – 26-cyfrowy numer rachunku bankowego w formacie NRB (bez liter `PL`, bez spacji; walidowany algorytmem Modulo 97).
* **Parametry query:**
  - `date` *(opcjonalny)* – data weryfikacji w formacie `YYYY-MM-DD` (domyślnie: bieżąca data wg czasu polskiego `Europe/Warsaw`).
  - `apiKey` *(opcjonalny)* – alternatywny sposób przekazania klucza klienta w query stringu (`?apiKey=...`).

#### Przykłady żądań:
```bash
# 1. Sprawdzenie z domyślnym limitem IP (na bieżący dzień)
curl -X GET "http://localhost:8080/api/check/nip/5260250995/bank-account/02102010260000140201648043"

# 2. Sprawdzenie na konkretną datę
curl -X GET "http://localhost:8080/api/check/nip/5260250995/bank-account/02102010260000140201648043?date=2026-10-14"

# 3. Z kluczem klienta w ścieżce URL (dla systemów ERP/FK bez własnych nagłówków)
curl -X GET "http://localhost:8080/k/klucz-klienta-standard/api/check/nip/5260250995/bank-account/02102010260000140201648043"

# 4. Z kluczem klienta w parametrze query
curl -X GET "http://localhost:8080/api/check/nip/5260250995/bank-account/02102010260000140201648043?apiKey=klucz-klienta-standard"
```

#### Przykład odpowiedzi (200 OK):
```json
{
  "result": {
    "accountAssigned": "TAK",
    "requestDateTime": "14-10-2026 19:30:15",
    "requestId": "a7b3c2e1f4094d128b"
  }
}
```

* `accountAssigned`: `"TAK"` (rachunek figuruje na Białej Liście) lub `"NIE"` (brak powiązania).
* `requestId`: unikalny identyfikator zapytania służący jako dowód należytej staranności w celach audytowych.

---

### 3.2. Weryfikacja po numerze REGON

```http
GET /api/check/regon/{regon}/bank-account/{bankAccount}?date=YYYY-MM-DD
GET /k/{apiKey}/api/check/regon/{regon}/bank-account/{bankAccount}?date=YYYY-MM-DD
```

* **Parametry ścieżki:**
  - `regon` – 9- lub 14-cyfrowy numer REGON podmiotu (walidowany wagami kontrolnymi).
  - Pozostałe parametry i format odpowiedzi identyczne jak dla NIP.

#### Przykład żądania:
```bash
curl -X GET "http://localhost:8080/api/check/regon/012100784/bank-account/02102010260000140201648043?date=2026-10-14"
```

---

### 3.3. Sonda diagnostyczna i gotowości (`/healthz`)

```http
GET /healthz
```

Służy jako **Readiness** oraz **Liveness probe** w Azure Container Apps / Kubernetes:
* **`200 OK` (Healthy):** Zwracany, gdy co najmniej jeden dzień danych został w pełni załadowany do pamięci.
* **`503 Service Unavailable` (Starting):** Zwracany w trakcie rozruchu kontenera, zanim dane zostaną pobrane i zmapowane. Dzięki temu Ingress nie kieruje ruchu produkcyjnego do niegotowego kontenera.

```json
{
  "status": "Healthy",
  "loadedDates": ["20261014", "20261013", "20261012"],
  "memoryAllocatedMb": 24.51,
  "machineTime": "2026-10-14T17:35:00.0000000Z"
}
```

---

### 3.4. Kody błędów

Format i kody błędów są zgodne ze specyfikacją techniczną Ministerstwa Finansów:

| Kod błędu | Status HTTP | Opis |
| :--- | :--- | :--- |
| `WL-113` | `400 Bad Request` | Niepoprawny numer NIP (błędna długość, znaki lub suma kontrolna). |
| `WL-114` | `400 Bad Request` | Niepoprawny numer REGON (błędna długość 9/14 cyfr lub suma kontrolna). |
| `WL-115` | `400 Bad Request` | Niepoprawny rachunek bankowy NRB (błędna długość 26 cyfr lub suma kontrolna Modulo 97). |
| `WL-111` | `400 Bad Request` | Niepoprawny format daty (wymagany `YYYY-MM-DD`). |
| `RATE-LIMIT` | `429 Too Many Requests` | Przekroczono dzienny limit zapytań dla danego klucza/IP lub serwer przetwarza więcej niż 16 równoległych zapytań. |
| `WL-500` | `502 Bad Gateway` | Błąd komunikacji z oficjalną bramką Ministerstwa Finansów w trybie Fallback. |

---

## 4. Konfiguracja i zmienne środowiskowe

Konfiguracja aplikacji może być przekazywana przez plik `appsettings.json` lub bezpośrednio przez zmienne środowiskowe kontenera:

| Zmienna środowiskowa | Domyślna wartość | Opis |
| :--- | :--- | :--- |
| `MF_API_BASE_URL` | `https://wl-api.mf.gov.pl/` | Adres oficjalnego API Wykazu Podatników VAT do zapytań Fallback. |
| `BLOB_STORAGE_URI` | *(brak)* | URI kontenera Azure Blob Storage z wygenerowanymi plikami `.bin` i logami audytu. |
| `TABLE_STORAGE_URI` | *(brak)* | URI usługi Azure Table Storage do logowania audytowego. |
| `AUDIT_TABLE_NAME` | `whitelist_audit` | Nazwa tabeli w Table Storage. |
| `CLIENT_QUOTAS` | *(standard=1000, premium=50000)* | Dzienne limity zapytań per klucz. Format: `klucz1:1000,klucz2:50000;klucz3=100000`. |
| `DOTNET_GCHeapHardLimit` | `0x30000000` (768 MB) | Twardy limit pamięci sterty GC (ochrona przed OOM w kontenerze 1.0 GiB). |
| `TZ` | `Europe/Warsaw` | Strefa czasowa kontenera. |

---

## 5. Wdrożenie w Azure Container Apps

### 5.1. Wymagania dotyczące zasobów i bezpieczeństwa
* **vCPU:** `0.5`
* **RAM:** `1.0 GiB`
* **Skalowanie:** `0..2` replik (skalowanie do zera przy braku ruchu – zerowy koszt w nocy).
* **Tożsamość zarządzana (Managed Identity):**
  Aplikacja nie przechowuje kluczy dostępowych do Storage. Używa tożsamości `SystemAssigned` kontenera i biblioteki `Azure.Identity (DefaultAzureCredential)`. Wymagane role RBAC:
  - `Storage Blob Data Contributor` (odczyt/zapis cache `.bin` i logów `.jsonl`)
  - `Storage Table Data Contributor` (zapis audytu transakcji do Azure Tables)

---

### 5.2. Szybkie wdrożenie automatyczne (Skrypty wdrożeniowe)

W repozytorium przygotowano gotowe skrypty automatyzujące pełną procedurę wdrożenia (tworzenie grupy zasobów, Storage Account, Bloba `whitelist`, tabeli `whitelist_audit`, Container App oraz uprawnień RBAC dla Managed Identity):

#### Dla systemu Linux / macOS (Bash):
```bash
chmod +x scripts/deploy-azure.sh
./scripts/deploy-azure.sh
```
Opcjonalna parametryzacja:
```bash
RESOURCE_GROUP="rg-mojafirma-whitelist" \
LOCATION="polandcentral" \
CONTAINER_APP_NAME="ca-whitelist-api" \
./scripts/deploy-azure.sh
```

#### Dla systemu Windows (PowerShell):
Skrypt [`scripts/deploy-azure.ps1`](file:///home/tom/Pulpit/Bia%C5%82aLista/scripts/deploy-azure.ps1) działa w PowerShell 5.1 oraz PowerShell 7+:
```powershell
.\scripts\deploy-azure.ps1
```
Opcjonalna parametryzacja przez parametry polecenia:
```powershell
.\scripts\deploy-azure.ps1 `
    -ResourceGroup "rg-mojafirma-whitelist" `
    -Location "polandcentral" `
    -ContainerAppName "ca-whitelist-api"
```

---

### 5.3. Wdrożenie krok po kroku (Azure CLI)

Jeśli wolisz wykonać poszczególne kroki ręcznie:

```bash
# 1. Zmienne
RESOURCE_GROUP="rg-whitelist-pl"
LOCATION="polandcentral"
CONTAINER_APP_NAME="ca-whitelist-api"
ENVIRONMENT_NAME="cae-whitelist-env"
STORAGE_ACCOUNT="stwhitelist$RANDOM"

# 2. Grupa zasobów
az group create --name $RESOURCE_GROUP --location $LOCATION

# 3. Konto Azure Storage (Blob + Table)
az storage account create \
  --name $STORAGE_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --location $LOCATION \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-blob-public-access false

STORAGE_ID=$(az storage account show --name $STORAGE_ACCOUNT --resource-group $RESOURCE_GROUP --query id -o tsv)

az storage container create --name whitelist --account-name $STORAGE_ACCOUNT --auth-mode login
az storage table create --name whitelist_audit --account-name $STORAGE_ACCOUNT --auth-mode login

# 4. Środowisko Azure Container Apps
az containerapp env create \
  --name $ENVIRONMENT_NAME \
  --resource-group $RESOURCE_GROUP \
  --location $LOCATION

# 5. Utworzenie aplikacji z tożsamością Managed Identity
az containerapp create \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --environment $ENVIRONMENT_NAME \
  --source . \
  --target-port 8080 \
  --ingress external \
  --cpu 0.5 \
  --memory 1.0Gi \
  --min-replicas 0 \
  --max-replicas 2 \
  --system-assigned \
  --env-vars \
    BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/whitelist" \
    TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
    AUDIT_TABLE_NAME="whitelist_audit" \
    CLIENT_QUOTAS="erp-standard:5000,erp-premium:50000" \
    TZ="Europe/Warsaw" \
    DOTNET_GCHeapHardLimit="0x30000000"

# 6. Nadanie uprawnień RBAC tożsamości kontenera
PRINCIPAL_ID=$(az containerapp identity show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --query principalId -o tsv)

az role assignment create \
  --assignee $PRINCIPAL_ID \
  --role "Storage Blob Data Contributor" \
  --scope $STORAGE_ID

az role assignment create \
  --assignee $PRINCIPAL_ID \
  --role "Storage Table Data Contributor" \
  --scope $STORAGE_ID

# 7. Restart rewizji kontenera (aby załadował uprawnienia RBAC)
az containerapp revision restart \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --revision $(az containerapp show -n $CONTAINER_APP_NAME -g $RESOURCE_GROUP --query properties.latestRevisionName -o tsv)
```

---

### 5.4. Weryfikacja wdrożenia

1. **Pobranie adresu URL:**
   ```bash
   APP_URL=$(az containerapp show -n $CONTAINER_APP_NAME -g $RESOURCE_GROUP --query properties.configuration.ingress.fqdn -o tsv)
   echo "Adres API: https://${APP_URL}"
   ```

2. **Sonda gotowości:**
   ```bash
   curl -i "https://${APP_URL}/healthz"
   ```

3. **Weryfikacja rachunku bankowego (NIP):**
   ```bash
   curl -i "https://${APP_URL}/k/erp-standard/api/check/nip/5260250995/bank-account/02102010260000140201648043"
   ```

---

### 5.5. Ciągła integracja i wdrożenie (CI/CD)
W katalogu `.github/workflows/` przygotowane są dwa przepływy GitHub Actions:
- **`ci.yml`**: Uruchamiany przy każdym Pull Requeście i wypchnięciu do gałęzi `main`. Przywraca pakiety, kompiluje projekt w konfiguracji `Release` i uruchamia 58 testów jednostkowych xUnit z raportem pokrycia kodu.
- **`deploy.yml`**: Uruchamiany automatycznie po przejściu testów na gałęzi `main` – loguje się do Azure przy użyciu tożsamości federacyjnej OIDC (`azure/login@v2`) i wdraża nową wersję do Azure Container Apps.

---

## 6. Uruchomienie lokalne i testy

### Wymagania:
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Uruchomienie testów jednostkowych:
```bash
dotnet test
```

### Uruchomienie serwera lokalnie:
```bash
dotnet run --project WhiteListApi.csproj
```
Dzięki profilowi w `Properties/launchSettings.json` aplikacja uruchamia się bezpośrednio pod adresem:
`http://localhost:8080`

> [!NOTE]
> Zaraz po uruchomieniu endpoint diagnostyczny `GET http://localhost:8080/healthz` zwraca status **HTTP 503 Service Unavailable** (`"status": "Starting"`). W tle usługa `WhiteListSyncBackgroundService` pobiera paczki `.7z` z MF i konwertuje je do lokalnych plików `.bin`. Po ukończeniu synchronizacji (zwykle kilkanaście–kilkadziesiąt sekund przy pierwszym pobraniu) status przechodzi w **HTTP 200 OK** (`"status": "Healthy"`).

### 6.1. Gdzie zapisywane są pliki lokalnie?

Podczas działania lokalnego pliki są przechowywane w dedykowanym katalogu tymczasowym systemu:
* **Linux:** `/tmp/whitelist_data/`
* **Windows:** `%TEMP%\whitelist_data\` (np. `C:\Users\<Użytkownik>\AppData\Local\Temp\whitelist_data\`)

W katalogu tym tworzone są:
* `{RRRRMMDD}.bin` (np. `20261014.bin`) – zoptymalizowany plik binarny zawierający wyłącznie posortowane 128-bitowe hashe (`UInt128` Big-Endian). Mapowany do pamięci przez `MemoryMappedFile` i przeszukiwany binarnie w gorącej ścieżce zapytania.
* `{RRRRMMDD}.meta.json` (np. `20261014.meta.json`) – plik metadanych JSON zawierający liczbę iteracji SHA-512 oraz listę masek rachunków wirtualnych przypisanych do banków.
* `{RRRRMMDD}.7z.tmp` i `{RRRRMMDD}.bin.tmp` – pliki tymczasowe tworzone wyłącznie na czas pobierania i strumieniowego parsowania paczki z MF, usuwane natychmiast po wygenerowaniu właściwego pliku `.bin`.

**Zasady retencji i usuwania:**
Aplikacja utrzymuje w katalogu lokalnym pliki dla **3 ostatnich dni** (dzisiaj, wczoraj, przedwczoraj wg strefy `Europe/Warsaw`). Starsze pliki `.bin` i `.meta.json` są automatycznie usuwane z dysku przez `MultiDayWhiteListManager.EnforceRetentionPolicy()`.

**W chmurze (Azure Container Apps):**
Jeśli skonfigurowano zmienną środowiskową `BLOB_STORAGE_URI`, przetworzone pliki `.bin` oraz `.meta.json` są automatycznie wysyłane do **Azure Blob Storage**. Kolejne uruchomienia lub nowe instancje kontenera pobierają gotowe pliki binarne bezpośrednio z Blob Storage w zaledwie 1–2 sekundy (pomijając czasochłonne pobieranie i rozpakowywanie `.7z` z serwerów MF).

### Budowanie obrazu Docker lokalnie:
```bash
docker build -t whitelist-api:latest .
docker run -p 8080:8080 -e CLIENT_QUOTAS="test-key:1000" whitelist-api:latest
```
