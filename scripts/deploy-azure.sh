#!/usr/bin/env bash
# ==============================================================================
# Skrypt automatycznego wdrożenia WhiteListApi do Azure Container Apps
# ==============================================================================
set -euo pipefail

# 1. Konfiguracja zmiennych (można nadpisać przez zmienne środowiskowe)
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-whitelist-pl}"
LOCATION="${LOCATION:-polandcentral}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-ca-whitelist-api}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-cae-whitelist-env}"
BLOB_CONTAINER_NAME="whitelist"
AUDIT_TABLE_NAME="whitelist_audit"

# Generowanie unikalnej nazwy Storage Account jeśli nie podano
if [[ -z "${STORAGE_ACCOUNT:-}" ]]; then
  UNIQUE_HASH=$(echo -n "${RESOURCE_GROUP}${LOCATION}$(date +%s)" | sha256sum | tr -dc 'a-z0-9' | head -c 8)
  STORAGE_ACCOUNT="stwhitelist${UNIQUE_HASH}"
fi

echo "=================================================================="
echo "Rozpoczynam wdrożenie Białej Listy Podatników VAT do Azure"
echo "Grupa zasobów:    ${RESOURCE_GROUP}"
echo "Region:           ${LOCATION}"
echo "Storage Account:  ${STORAGE_ACCOUNT}"
echo "Container App:    ${CONTAINER_APP_NAME}"
echo "Environment:      ${ENVIRONMENT_NAME}"
echo "=================================================================="

# 2. Sprawdzenie Azure CLI
if ! command -v az &> /dev/null; then
  echo "BŁĄD: Narzędzie Azure CLI ('az') nie jest zainstalowane."
  exit 1
fi

if ! az account show &> /dev/null; then
  echo "Brak aktywnej sesji Azure CLI. Logowanie..."
  az login --use-device-code
fi

SUBSCRIPTION_NAME=$(az account show --query name -o tsv)
echo "Aktywna subskrypcja: ${SUBSCRIPTION_NAME}"

# 3. Tworzenie Grupy Zasobów
echo "--> [1/6] Tworzenie grupy zasobów '${RESOURCE_GROUP}' w regionie '${LOCATION}'..."
az group create \
  --name "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --output none

# 4. Tworzenie konta Storage (Blob + Table)
echo "--> [2/6] Tworzenie konta Azure Storage '${STORAGE_ACCOUNT}'..."
az storage account create \
  --name "${STORAGE_ACCOUNT}" \
  --resource-group "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-blob-public-access false \
  --output none

STORAGE_ID=$(az storage account show --name "${STORAGE_ACCOUNT}" --resource-group "${RESOURCE_GROUP}" --query id -o tsv)

echo "--> [3/6] Tworzenie kontenera Blob '${BLOB_CONTAINER_NAME}' i tabeli '${AUDIT_TABLE_NAME}'..."
az storage container create \
  --name "${BLOB_CONTAINER_NAME}" \
  --account-name "${STORAGE_ACCOUNT}" \
  --auth-mode login \
  --output none

az storage table create \
  --name "${AUDIT_TABLE_NAME}" \
  --account-name "${STORAGE_ACCOUNT}" \
  --auth-mode login \
  --output none

# 5. Tworzenie środowiska Container Apps
echo "--> [4/6] Przygotowanie środowiska Azure Container Apps '${ENVIRONMENT_NAME}'..."
az containerapp env create \
  --name "${ENVIRONMENT_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --output none

# 6. Wdrożenie Container App ze źródła
echo "--> [5/6] Budowanie i wdrażanie aplikacji '${CONTAINER_APP_NAME}'..."
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

az containerapp create \
  --name "${CONTAINER_APP_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --environment "${ENVIRONMENT_NAME}" \
  --source "${ROOT_DIR}" \
  --target-port 8080 \
  --ingress external \
  --cpu 0.5 \
  --memory 1.0Gi \
  --min-replicas 0 \
  --max-replicas 2 \
  --system-assigned \
  --env-vars \
    BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/${BLOB_CONTAINER_NAME}" \
    TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
    AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME}" \
    CLIENT_QUOTAS="erp-standard:5000,erp-premium:50000" \
    TZ="Europe/Warsaw" \
    DOTNET_GCHeapHardLimit="0x30000000" \
  --output none

# 7. Konfiguracja uprawnień RBAC dla tożsamości zarządzanej kontenera
echo "--> [6/6] Konfiguracja uprawnień RBAC (Managed Identity)..."
PRINCIPAL_ID=$(az containerapp identity show \
  --name "${CONTAINER_APP_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --query principalId -o tsv)

echo "Tożsamość Principal ID: ${PRINCIPAL_ID}"

# Przypisanie ról z obsługą idempotencji
az role assignment create \
  --assignee "${PRINCIPAL_ID}" \
  --role "Storage Blob Data Contributor" \
  --scope "${STORAGE_ID}" \
  --output none || true

az role assignment create \
  --assignee "${PRINCIPAL_ID}" \
  --role "Storage Table Data Contributor" \
  --scope "${STORAGE_ID}" \
  --output none || true

# Restart rewizji kontenera
LATEST_REV=$(az containerapp show --name "${CONTAINER_APP_NAME}" --resource-group "${RESOURCE_GROUP}" --query properties.latestRevisionName -o tsv)
if [[ -n "${LATEST_REV}" && "${LATEST_REV}" != "null" ]]; then
  echo "Przeładowanie rewizji kontenera (${LATEST_REV})..."
  az containerapp revision restart \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --revision "${LATEST_REV}" \
    --output none || true
fi

# 8. Podsumowanie i linki
FQDN=$(az containerapp show --name "${CONTAINER_APP_NAME}" --resource-group "${RESOURCE_GROUP}" --query properties.configuration.ingress.fqdn -o tsv)

echo ""
echo "=================================================================="
echo "SUKCES! Wdrożenie zakończone pomyślnie."
echo "Publiczny adres URL: https://${FQDN}"
echo "=================================================================="
echo ""
echo "Sprawdzenie statusu gotowości:"
echo "  curl -i https://${FQDN}/healthz"
echo ""
echo "Przykładowe sprawdzenie konta NIP:"
echo "  curl -i https://${FQDN}/k/erp-standard/api/check/nip/5260250995/bank-account/02102010260000140201648043"
echo ""
