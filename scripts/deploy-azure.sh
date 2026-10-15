#!/usr/bin/env bash
# ==============================================================================
# Skrypt automatycznego wdrożenia WhiteListApi do Azure Container Apps
# ==============================================================================
set -euo pipefail

# Konfiguracja zmiennych (można nadpisać przez zmienne środowiskowe)
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-whitelist-pl}"
LOCATION="${LOCATION:-polandcentral}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-ca-whitelist-api}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-cae-whitelist-env}"
BLOB_CONTAINER_NAME="${BLOB_CONTAINER_NAME:-whitelist}"
AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME:-whitelistAudit}"
CLIENT_QUOTAS="${CLIENT_QUOTAS:-erp-standard:5000,erp-premium:50000}"
DEPLOY_MODE="${DEPLOY_MODE:-source}"
ACR_NAME="${ACR_NAME:-}"
ACR_RESOURCE_GROUP="${ACR_RESOURCE_GROUP:-$RESOURCE_GROUP}"
IMAGE="${IMAGE:-}"
IMAGE_TAG="${IMAGE_TAG:-$(date -u +%Y%m%d%H%M%S)}"
DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-}"
DOCKERHUB_TOKEN="${DOCKERHUB_TOKEN:-}"
IMAGE_REGISTRY=""

if [[ "$DEPLOY_MODE" != "source" && "$DEPLOY_MODE" != "image" && "$DEPLOY_MODE" != "build-image" ]]; then
  echo "BŁĄD: DEPLOY_MODE musi mieć wartość source, image lub build-image." >&2
  exit 1
fi
if [[ "$DEPLOY_MODE" == "image" ]]; then
  if [[ "$IMAGE" =~ ^([a-z0-9]{5,50})\.azurecr\.io/.+ ]]; then
    ACR_NAME="${BASH_REMATCH[1]}"
    IMAGE_REGISTRY="acr"
  elif [[ "$IMAGE" =~ ^(docker\.io/|index\.docker\.io/)?[a-z0-9][a-z0-9_.-]*/[a-z0-9][a-z0-9_.-]*(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?$ ]]; then
    IMAGE_REGISTRY="dockerhub"
  else
    echo "BŁĄD: IMAGE musi wskazywać obraz w ACR lub Docker Hub, np. docker.io/firma/whitelist-api:v1." >&2
    exit 1
  fi
fi
if [[ "$DEPLOY_MODE" == "build-image" && -z "$ACR_NAME" ]]; then
  echo "BŁĄD: Dla DEPLOY_MODE=build-image podaj unikalną nazwę ACR przez ACR_NAME." >&2
  exit 1
fi
if [[ ( "$DEPLOY_MODE" == "build-image" || "$IMAGE_REGISTRY" == "acr" ) && ! "$ACR_NAME" =~ ^[a-z0-9]{5,50}$ ]]; then
  echo "BŁĄD: ACR_NAME musi mieć 5-50 znaków alfanumerycznych." >&2
  exit 1
fi
if [[ "$DEPLOY_MODE" == "image" && -z "$IMAGE" ]]; then
  echo "BŁĄD: Dla DEPLOY_MODE=image podaj IMAGE." >&2
  exit 1
fi
if [[ "$IMAGE_REGISTRY" == "dockerhub" && ( -n "$DOCKERHUB_USERNAME" || -n "$DOCKERHUB_TOKEN" ) && ( -z "$DOCKERHUB_USERNAME" || -z "$DOCKERHUB_TOKEN" ) ]]; then
  echo "BŁĄD: Dla prywatnego Docker Hub podaj oba: DOCKERHUB_USERNAME i DOCKERHUB_TOKEN." >&2
  exit 1
fi

# Generowanie unikalnej nazwy Storage Account jeśli nie podano
if [[ -z "${STORAGE_ACCOUNT:-}" ]]; then
  UNIQUE_HASH=$(printf '%s' "${RESOURCE_GROUP}${LOCATION}$(date +%s%N)" | sha256sum | cut -c1-16)
  STORAGE_ACCOUNT="stwl${UNIQUE_HASH}"
fi

az_checked() {
  local step="$1"
  shift
  local status

  if az "$@"; then
    return 0
  else
    status=$?
    printf 'Niepowodzenie: %s (kod Azure CLI: %s).\n' "$step" "$status" >&2
    return "$status"
  fi
}

az_value() {
  local step="$1"
  shift
  local result status

  if result=$(az "$@"); then
    printf '%s' "$result"
  else
    status=$?
    printf 'Niepowodzenie: %s (kod Azure CLI: %s).\n' "$step" "$status" >&2
    return "$status"
  fi
}

if ! command -v az &> /dev/null; then
  echo "BŁĄD: Narzędzie Azure CLI ('az') nie jest zainstalowane."
  exit 1
fi

if [[ ! "$STORAGE_ACCOUNT" =~ ^[a-z0-9]{3,24}$ ]]; then
  echo "BŁĄD: STORAGE_ACCOUNT musi mieć 3-24 znaki (małe litery i cyfry)." >&2
  exit 1
fi
if [[ ! "$AUDIT_TABLE_NAME" =~ ^[A-Za-z][A-Za-z0-9]{2,62}$ ]]; then
  echo "BŁĄD: AUDIT_TABLE_NAME musi mieć 3-63 znaki i zaczynać się literą." >&2
  exit 1
fi

echo "============================================================"
echo "Wdrożenie WhiteListApi do Azure Container Apps"
echo "Resource Group:    ${RESOURCE_GROUP}"
echo "Region:            ${LOCATION}"
echo "Storage Account:   ${STORAGE_ACCOUNT}"
echo "Blob Container:    ${BLOB_CONTAINER_NAME}"
echo "Audit Table:       ${AUDIT_TABLE_NAME}"
echo "Container App:     ${CONTAINER_APP_NAME}"
echo "Environment:       ${ENVIRONMENT_NAME}"
echo "============================================================"

SUBSCRIPTION_ID=$(az_value 'Pobieranie aktywnej subskrypcji' account show --query id -o tsv)
if [[ -z "$SUBSCRIPTION_ID" ]]; then
  echo "Brak aktywnego logowania Azure CLI. Uruchom az login --use-device-code." >&2
  exit 1
fi
echo "Aktywna subskrypcja: ${SUBSCRIPTION_ID}"

echo "[1/6] Grupa zasobów..."
az_checked 'Tworzenie/sprawdzenie Resource Group' group create \
  --name "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  -o none

# Rejestracja wymaganych dostawców Azure
REQUIRED_PROVIDERS=(
  "Microsoft.Storage"
  "Microsoft.App"
  "Microsoft.OperationalInsights"
  "Microsoft.ContainerRegistry"
  "Microsoft.ManagedIdentity"
)

for provider in "${REQUIRED_PROVIDERS[@]}"; do
  echo "Sprawdzanie: ${provider}"
  status=$(az_value "Sprawdzanie dostawcy ${provider}" provider show \
    --namespace "$provider" --query registrationState -o tsv)

  if [[ "$status" != "Registered" ]]; then
    echo "Rejestrowanie: ${provider}"
    az_checked "Rejestrowanie dostawcy ${provider}" provider register \
      --namespace "$provider" --wait
  fi

  echo "${provider} - OK"
done

echo "[2/6] Azure Storage Account..."
EXISTING_STORAGE=$(az_value 'Sprawdzanie kont Storage' storage account list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[?name=='${STORAGE_ACCOUNT}'].name | [0]" -o tsv)
if [[ -z "$EXISTING_STORAGE" ]]; then
  az_checked 'Tworzenie Storage Account' storage account create \
    --name "$STORAGE_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku Standard_LRS \
    --kind StorageV2 \
    --allow-blob-public-access false \
    -o none
else
  echo "Konto Storage już istnieje w tej grupie - wykorzystuję je."
fi

STORAGE_ID=$(az_value 'Pobieranie identyfikatora Storage Account' storage account show \
  --name "$STORAGE_ACCOUNT" --resource-group "$RESOURCE_GROUP" --query id -o tsv)
if [[ -z "$STORAGE_ID" ]]; then
  echo "Storage Account nie ma poprawnego identyfikatora zasobu." >&2
  exit 1
fi

echo "[3/6] Blob Container i Table Storage..."
az_checked 'Tworzenie kontenera Blob' storage container create \
  --name "${BLOB_CONTAINER_NAME}" \
  --account-name "${STORAGE_ACCOUNT}" \
  --auth-mode login \
  -o none

az_checked 'Tworzenie tabeli audytu' storage table create \
  --name "${AUDIT_TABLE_NAME}" \
  --account-name "${STORAGE_ACCOUNT}" \
  --auth-mode login \
  -o none

echo "[4/6] Container Apps Environment..."
ENVIRONMENT_NAMES=$(az_value 'Lista istniejących środowisk Container Apps' resource list \
  --resource-group "$RESOURCE_GROUP" \
  --resource-type Microsoft.App/managedEnvironments \
  --query '[].name' -o tsv)
if [[ $'\n'"$ENVIRONMENT_NAMES"$'\n' == *$'\n'"$ENVIRONMENT_NAME"$'\n'* ]]; then
  echo "Container Apps Environment już istnieje - wykorzystuję je."
else
  echo "Brak środowiska '${ENVIRONMENT_NAME}' na liście ARM. Tworzenie..."
  az_checked 'Tworzenie Container Apps Environment' containerapp env create \
    --name "$ENVIRONMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" -o none
fi

echo "[5/6] Budowanie obrazu / wdrażanie aplikacji..."
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ "$DEPLOY_MODE" == "source" ]]; then
  az_checked 'Budowanie i wdrażanie aplikacji ze źródeł' containerapp up \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --location "${LOCATION}" \
    --environment "${ENVIRONMENT_NAME}" \
    --source "${ROOT_DIR}" \
    --target-port 8080 \
    --ingress external \
    --env-vars \
      BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/${BLOB_CONTAINER_NAME}" \
      TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
      AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME}" \
      CLIENT_QUOTAS="${CLIENT_QUOTAS}" \
      TZ=Europe/Warsaw \
      DOTNET_GCHeapHardLimit=0x30000000
else
  if [[ "$DEPLOY_MODE" == "build-image" ]]; then
    if ! az acr show --name "$ACR_NAME" --resource-group "$ACR_RESOURCE_GROUP" -o none 2>/dev/null; then
      az_checked 'Tworzenie Azure Container Registry' acr create \
        --name "$ACR_NAME" --resource-group "$ACR_RESOURCE_GROUP" \
        --location "$LOCATION" --sku Basic --admin-enabled false -o none
    fi
    IMAGE="${ACR_NAME}.azurecr.io/whitelist-api:${IMAGE_TAG}"
    az_checked 'Budowanie i publikowanie obrazu Docker w ACR' acr build \
      --registry "$ACR_NAME" --image "whitelist-api:${IMAGE_TAG}" "$ROOT_DIR"
    IMAGE_REGISTRY="acr"
  fi

  if [[ "$IMAGE_REGISTRY" == "acr" ]]; then
  ACR_ID=$(az_value 'Pobieranie identyfikatora ACR' acr show \
    --name "$ACR_NAME" --resource-group "$ACR_RESOURCE_GROUP" --query id -o tsv)
  ACR_SERVER="${ACR_NAME}.azurecr.io"
  PULL_IDENTITY_NAME="${CONTAINER_APP_NAME}-acr-pull"
  if ! az identity show --name "$PULL_IDENTITY_NAME" --resource-group "$RESOURCE_GROUP" -o none 2>/dev/null; then
    az_checked 'Tworzenie Managed Identity do pobierania obrazu' identity create \
      --name "$PULL_IDENTITY_NAME" --resource-group "$RESOURCE_GROUP" \
      --location "$LOCATION" -o none
  fi
  PULL_IDENTITY_ID=$(az_value 'Pobieranie Managed Identity ACR' identity show \
    --name "$PULL_IDENTITY_NAME" --resource-group "$RESOURCE_GROUP" --query id -o tsv)
  PULL_PRINCIPAL_ID=$(az_value 'Pobieranie Principal ID ACR' identity show \
    --name "$PULL_IDENTITY_NAME" --resource-group "$RESOURCE_GROUP" --query principalId -o tsv)
  ACR_ROLES=$(az_value 'Sprawdzanie roli AcrPull' role assignment list \
    --scope "$ACR_ID" --query "[?principalId=='${PULL_PRINCIPAL_ID}'].roleDefinitionName" -o tsv)
  if [[ $'\n'"$ACR_ROLES"$'\n' != *$'\n'AcrPull$'\n'* ]]; then
    az_checked 'Nadawanie roli AcrPull' role assignment create \
      --assignee-object-id "$PULL_PRINCIPAL_ID" \
      --assignee-principal-type ServicePrincipal \
      --role AcrPull --scope "$ACR_ID" -o none
  fi

  if az containerapp show --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" -o none 2>/dev/null; then
    az_checked 'Przypisanie tożsamości do pobierania obrazu' containerapp identity assign \
      --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
      --system-assigned --user-assigned "$PULL_IDENTITY_ID" -o none
    az_checked 'Konfiguracja rejestru obrazu' containerapp registry set \
      --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
      --server "$ACR_SERVER" --identity "$PULL_IDENTITY_ID"
    az_checked 'Aktualizacja aplikacji do obrazu z ACR' containerapp update \
      --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
      --image "$IMAGE" --set-env-vars \
        BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/${BLOB_CONTAINER_NAME}" \
        TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
        AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME}" CLIENT_QUOTAS="${CLIENT_QUOTAS}" \
        TZ=Europe/Warsaw DOTNET_GCHeapHardLimit=0x30000000
  else
    az_checked 'Utworzenie aplikacji z obrazem ACR' containerapp create \
      --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
      --environment "$ENVIRONMENT_NAME" --image "$IMAGE" \
      --target-port 8080 --ingress external \
      --system-assigned --user-assigned "$PULL_IDENTITY_ID" \
      --registry-server "$ACR_SERVER" --registry-identity "$PULL_IDENTITY_ID" \
      --env-vars \
        BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/${BLOB_CONTAINER_NAME}" \
        TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
        AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME}" CLIENT_QUOTAS="${CLIENT_QUOTAS}" \
        TZ=Europe/Warsaw DOTNET_GCHeapHardLimit=0x30000000 -o none
  fi
  else
    DOCKERHUB_SERVER="index.docker.io"
    REGISTRY_ARGS=()
    if [[ -n "$DOCKERHUB_USERNAME" ]]; then
      REGISTRY_ARGS+=(--registry-server "$DOCKERHUB_SERVER" \
        --registry-username "$DOCKERHUB_USERNAME" --registry-password "$DOCKERHUB_TOKEN")
    fi

    if az containerapp show --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" -o none 2>/dev/null; then
      if [[ -n "$DOCKERHUB_USERNAME" ]]; then
        az_checked 'Konfiguracja prywatnego repozytorium Docker Hub' containerapp registry set \
          --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
          --server "$DOCKERHUB_SERVER" --username "$DOCKERHUB_USERNAME" \
          --password "$DOCKERHUB_TOKEN"
      fi
      az_checked 'Aktualizacja aplikacji do obrazu Docker Hub' containerapp update \
        --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
        --image "$IMAGE" --set-env-vars \
          BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/${BLOB_CONTAINER_NAME}" \
          TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
          AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME}" CLIENT_QUOTAS="${CLIENT_QUOTAS}" \
          TZ=Europe/Warsaw DOTNET_GCHeapHardLimit=0x30000000
    else
      az_checked 'Utworzenie aplikacji z obrazem Docker Hub' containerapp create \
        --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP" \
        --environment "$ENVIRONMENT_NAME" --image "$IMAGE" \
        --target-port 8080 --ingress external --system-assigned \
        "${REGISTRY_ARGS[@]}" \
        --env-vars \
          BLOB_STORAGE_URI="https://${STORAGE_ACCOUNT}.blob.core.windows.net/${BLOB_CONTAINER_NAME}" \
          TABLE_STORAGE_URI="https://${STORAGE_ACCOUNT}.table.core.windows.net/" \
          AUDIT_TABLE_NAME="${AUDIT_TABLE_NAME}" CLIENT_QUOTAS="${CLIENT_QUOTAS}" \
          TZ=Europe/Warsaw DOTNET_GCHeapHardLimit=0x30000000 -o none
    fi
  fi
  echo "Używany obraz: ${IMAGE}"
fi

echo "[6/6] Managed Identity i RBAC..."
az_checked 'Przypisanie system-assigned Managed Identity' containerapp identity assign \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --system-assigned -o none

PRINCIPAL_ID=$(az_value 'Odczyt Managed Identity' containerapp identity show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query principalId -o tsv)
if [[ -z "$PRINCIPAL_ID" || "$PRINCIPAL_ID" == "null" ]]; then
  echo "Container App nie ma Principal ID (Managed Identity)." >&2
  exit 1
fi
echo "Principal ID: ${PRINCIPAL_ID}"

ROLE_NAMES=("Storage Blob Data Contributor" "Storage Table Data Contributor")
ASSIGNED_ROLES=$(az_value 'Odczyt istniejących przypisań RBAC' role assignment list \
  --scope "$STORAGE_ID" \
  --query "[?principalId=='${PRINCIPAL_ID}'].roleDefinitionName" -o tsv)
for role in "${ROLE_NAMES[@]}"; do
  if [[ $'\n'"$ASSIGNED_ROLES"$'\n' == *$'\n'"$role"$'\n'* ]]; then
    echo "Rola już przypisana: ${role}"
    continue
  fi

  az_checked "Przypisanie roli ${role}" role assignment create \
    --assignee-object-id "$PRINCIPAL_ID" \
    --assignee-principal-type ServicePrincipal \
    --role "$role" \
    --scope "$STORAGE_ID" -o none
done

az_checked 'Konfiguracja CPU, pamięci i liczby replik' containerapp update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --cpu 0.5 \
  --memory 1.0Gi \
  --min-replicas 0 \
  --max-replicas 2 -o none

FQDN=$(az_value 'Pobieranie adresu aplikacji' containerapp show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query properties.configuration.ingress.fqdn -o tsv)
if [[ -z "$FQDN" || "$FQDN" == "null" ]]; then
  echo "Wdrożenie zakończone, ale brak FQDN aplikacji. Sprawdź konfigurację ingress." >&2
  exit 1
fi

echo "Wdrożenie zasobów zakończone. Endpoint: https://${FQDN}"
echo "Sprawdzenie: curl -i https://${FQDN}/healthz"
echo "Przykład API: https://${FQDN}/k/bc/api/check/nip/5260250995/bank-account/02102010260000140201648043"
echo "Uwaga: RBAC może wymagać czasu na propagację. Sprawdź healthz i logi kontenera."
