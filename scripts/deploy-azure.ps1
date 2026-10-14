<#
.SYNOPSIS
    Skrypt automatycznego wdrożenia WhiteListApi do Azure Container Apps dla Windows PowerShell / PowerShell 7.
.DESCRIPTION
    Przygotowuje konto Azure Storage, tworzy kontener Blob i tabelę audytu, wdraża mikroserwis
    do Azure Container Apps z tożsamością Managed Identity i przypisuje role RBAC.
.PARAMETER ResourceGroup
    Nazwa grupy zasobów w Azure (domyślnie: rg-whitelist-pl).
.PARAMETER Location
    Region Azure (domyślnie: polandcentral).
.PARAMETER ContainerAppName
    Nazwa aplikacji Container App (domyślnie: ca-whitelist-api).
.PARAMETER EnvironmentName
    Nazwa środowiska Container Apps Environment (domyślnie: cae-whitelist-env).
.PARAMETER StorageAccount
    Nazwa konta Storage (opcjonalna, jeśli pusta zostanie wygenerowana unikalna).
.EXAMPLE
    .\scripts\deploy-azure.ps1
.EXAMPLE
    .\scripts\deploy-azure.ps1 -ResourceGroup "rg-produkcja" -Location "polandcentral"
#>

[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-whitelist-pl",
    [string]$Location = "polandcentral",
    [string]$ContainerAppName = "ca-whitelist-api",
    [string]$EnvironmentName = "cae-whitelist-env",
    [string]$StorageAccount = "",
    [string]$BlobContainerName = "whitelist",
    [string]$AuditTableName = "whitelist_audit"
)

$ErrorActionPreference = "Stop"

# 1. Generowanie unikalnej nazwy konta Storage (jeśli nie przekazano parametru)
if ([string]::IsNullOrWhiteSpace($StorageAccount)) {
    $randomHex = -join ((0..7) | ForEach-Object { '{0:x}' -f (Get-Random -Max 16) })
    $StorageAccount = "stwl$randomHex"
}

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "Rozpoczynam wdrożenie Białej Listy Podatników VAT do Azure" -ForegroundColor Cyan
Write-Host "Grupa zasobów:    $ResourceGroup"
Write-Host "Region:           $Location"
Write-Host "Storage Account:  $StorageAccount"
Write-Host "Container App:    $ContainerAppName"
Write-Host "Environment:      $EnvironmentName"
Write-Host "==================================================================" -ForegroundColor Cyan

# 2. Sprawdzenie Azure CLI
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Narzędzie Azure CLI ('az') nie zostało odnalezione. Zainstaluj je ze strony: https://learn.microsoft.com/cli/azure/install-azure-cli-windows"
    exit 1
}

$accountCheck = az account show --output json 2>$null
if (-not $accountCheck) {
    Write-Host "Brak aktywnej sesji Azure CLI. Logowanie..." -ForegroundColor Yellow
    az login --use-device-code
}

$subName = az account show --query name -o tsv
Write-Host "Aktywna subskrypcja: $subName" -ForegroundColor Green

# 3. Tworzenie Grupy Zasobów
Write-Host "`n--> [1/6] Tworzenie grupy zasobów '$ResourceGroup' w regionie '$Location'..." -ForegroundColor Yellow
az group create `
    --name $ResourceGroup `
    --location $Location `
    --output none

# 4. Tworzenie konta Storage (Blob + Table)
Write-Host "--> [2/6] Tworzenie konta Azure Storage '$StorageAccount'..." -ForegroundColor Yellow
az storage account create `
    --name $StorageAccount `
    --resource-group $ResourceGroup `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --allow-blob-public-access false `
    --output none

$storageId = az storage account show --name $StorageAccount --resource-group $ResourceGroup --query id -o tsv

Write-Host "--> [3/6] Tworzenie kontenera Blob '$BlobContainerName' i tabeli '$AuditTableName'..." -ForegroundColor Yellow
az storage container create `
    --name $BlobContainerName `
    --account-name $StorageAccount `
    --auth-mode login `
    --output none

az storage table create `
    --name $AuditTableName `
    --account-name $StorageAccount `
    --auth-mode login `
    --output none

# 5. Tworzenie środowiska Container Apps
Write-Host "--> [4/6] Przygotowanie środowiska Azure Container Apps '$EnvironmentName'..." -ForegroundColor Yellow
az containerapp env create `
    --name $EnvironmentName `
    --resource-group $ResourceGroup `
    --location $Location `
    --output none

# 6. Wdrożenie Container App ze źródła
Write-Host "--> [5/6] Budowanie i wdrażanie aplikacji '$ContainerAppName'..." -ForegroundColor Yellow
$rootDir = (Resolve-Path "$PSScriptRoot/..").Path

az containerapp create `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --environment $EnvironmentName `
    --source $rootDir `
    --target-port 8080 `
    --ingress external `
    --cpu 0.5 `
    --memory 1.0Gi `
    --min-replicas 0 `
    --max-replicas 2 `
    --system-assigned `
    --env-vars `
        BLOB_STORAGE_URI="https://$StorageAccount.blob.core.windows.net/$BlobContainerName" `
        TABLE_STORAGE_URI="https://$StorageAccount.table.core.windows.net/" `
        AUDIT_TABLE_NAME="$AuditTableName" `
        CLIENT_QUOTAS="erp-standard:5000,erp-premium:50000" `
        TZ="Europe/Warsaw" `
        DOTNET_GCHeapHardLimit="0x30000000" `
    --output none

# 7. Konfiguracja uprawnień RBAC dla tożsamości zarządzanej kontenera
Write-Host "--> [6/6] Konfiguracja uprawnień RBAC (Managed Identity)..." -ForegroundColor Yellow
$principalId = az containerapp identity show `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --query principalId -o tsv

Write-Host "Tożsamość Principal ID: $principalId" -ForegroundColor Green

az role assignment create `
    --assignee $principalId `
    --role "Storage Blob Data Contributor" `
    --scope $storageId `
    --output none 2>$null

az role assignment create `
    --assignee $principalId `
    --role "Storage Table Data Contributor" `
    --scope $storageId `
    --output none 2>$null

# Przeładowanie rewizji kontenera
$latestRev = az containerapp show --name $ContainerAppName --resource-group $ResourceGroup --query properties.latestRevisionName -o tsv
if ($latestRev -and $latestRev -ne "null") {
    Write-Host "Przeładowanie rewizji kontenera ($latestRev)..." -ForegroundColor Yellow
    az containerapp revision restart `
        --name $ContainerAppName `
        --resource-group $ResourceGroup `
        --revision $latestRev `
        --output none 2>$null
}

# 8. Podsumowanie i linki
$fqdn = az containerapp show --name $ContainerAppName --resource-group $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "SUKCES! Wdrożenie zakończone pomyślnie." -ForegroundColor Green
Write-Host "Publiczny adres URL: https://$fqdn" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Sprawdzenie statusu gotowości:" -ForegroundColor White
Write-Host "  curl -i https://$fqdn/healthz" -ForegroundColor Gray
Write-Host "  lub PowerShell:" -ForegroundColor White
Write-Host "  Invoke-RestMethod -Uri https://$fqdn/healthz" -ForegroundColor Gray
Write-Host ""
Write-Host "Przykładowe sprawdzenie konta NIP:" -ForegroundColor White
Write-Host "  Invoke-RestMethod -Uri https://$fqdn/k/erp-standard/api/check/nip/5260250995/bank-account/02102010260000140201648043" -ForegroundColor Gray
Write-Host ""
