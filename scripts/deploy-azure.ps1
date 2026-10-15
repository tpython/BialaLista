<#
.SYNOPSIS
    Wdrozenie WhiteListApi do Azure Container Apps (Windows PowerShell 5.1 / PowerShell 7).
.DESCRIPTION
    Przygotowuje lub wykorzystuje istniejace zasoby Azure Storage i Container Apps,
    buduje aplikacje ze zrodel, nadaje Managed Identity role do Blob i Table Storage.
    Po kazdym istotnym poleceniu Azure CLI sprawdza kod wyjscia.
#>

[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-whitelist-pl',
    [string]$Location = 'polandcentral',
    [string]$ContainerAppName = 'ca-whitelist-api',
    [string]$EnvironmentName = 'cae-whitelist-env',
    [string]$StorageAccount = '',
    [string]$BlobContainerName = 'whitelist',
    [string]$AuditTableName = 'whitelistAudit',
    [string]$ClientQuotas = 'erp-standard:5000,erp-premium:50000',
    [ValidateSet('source', 'image', 'build-image')][string]$DeployMode = 'source',
    [string]$AcrName = '',
    [string]$AcrResourceGroup = '',
    [string]$Image = '',
    [string]$ImageTag = '',
    [string]$DockerHubUsername = $env:DOCKERHUB_USERNAME,
    [string]$DockerHubToken = $env:DOCKERHUB_TOKEN
)

$ErrorActionPreference = 'Stop'

function Invoke-AzChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Niepowodzenie: $Step (kod Azure CLI: $LASTEXITCODE)."
    }
}

function Get-AzValue {
    param(
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Niepowodzenie: $Step (kod Azure CLI: $LASTEXITCODE)."
    }

    return ([string]($result -join "`n")).Trim()
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Nie znaleziono Azure CLI (az).'
}

if ([string]::IsNullOrWhiteSpace($StorageAccount)) {
    $StorageAccount = 'stwl' + [guid]::NewGuid().ToString('N').Substring(0, 16)
}

if ($StorageAccount -cnotmatch '^[a-z0-9]{3,24}$') {
    throw 'StorageAccount: nazwa musi miec 3-24 znaki (male litery i cyfry).'
}
if ($AuditTableName -cnotmatch '^[A-Za-z][A-Za-z0-9]{2,62}$') {
    throw 'AuditTableName: 3-63 znaki, tylko litery i cyfry, pierwszy znak litera.'
}
$imageRegistry = ''
if ($DeployMode -eq 'image') {
    if ($Image -cmatch '^([a-z0-9]{5,50})\.azurecr\.io/.+') {
        $imageRegistry = 'acr'
        $AcrName = $Matches[1]
    } elseif ($Image -match '^((docker\.io|index\.docker\.io)/)?[a-z0-9][a-z0-9_.-]*/[a-z0-9][a-z0-9_.-]*(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?$') {
        $imageRegistry = 'dockerhub'
    } else {
        throw 'Dla DeployMode=image podaj obraz z ACR lub Docker Hub, np. docker.io/firma/whitelist-api:v1.'
    }
}
if ($DeployMode -eq 'build-image' -and [string]::IsNullOrWhiteSpace($AcrName)) {
    throw 'Dla DeployMode=build-image podaj unikalna nazwe ACR przez -AcrName.'
}
if (($DeployMode -eq 'build-image' -or $imageRegistry -eq 'acr') -and $AcrName -cnotmatch '^[a-z0-9]{5,50}$') {
    throw 'AcrName musi miec 5-50 znakow alfanumerycznych.'
}
if ($DeployMode -eq 'image' -and [string]::IsNullOrWhiteSpace($Image)) {
    throw 'Dla DeployMode=image podaj -Image.'
}
if ($imageRegistry -eq 'dockerhub' -and
    ([string]::IsNullOrWhiteSpace($DockerHubUsername) -xor [string]::IsNullOrWhiteSpace($DockerHubToken))) {
    throw 'Dla prywatnego Docker Hub podaj oba: -DockerHubUsername i -DockerHubToken.'
}
if ([string]::IsNullOrWhiteSpace($ImageTag)) {
    $ImageTag = Get-Date -Format 'yyyyMMddHHmmss'
}
if ([string]::IsNullOrWhiteSpace($AcrResourceGroup)) {
    $AcrResourceGroup = $ResourceGroup
}

Write-Host '============================================================' -ForegroundColor Cyan
Write-Host 'Wdrozenie WhiteListApi do Azure Container Apps' -ForegroundColor Cyan
Write-Host "Resource Group:    $ResourceGroup"
Write-Host "Region:            $Location"
Write-Host "Storage Account:   $StorageAccount"
Write-Host "Blob Container:    $BlobContainerName"
Write-Host "Audit Table:       $AuditTableName"
Write-Host "Container App:     $ContainerAppName"
Write-Host "Environment:       $EnvironmentName"
Write-Host '============================================================' -ForegroundColor Cyan

$subscriptionId = Get-AzValue 'Pobieranie aktywnej subskrypcji' @(
    'account', 'show', '--query', 'id', '-o', 'tsv'
)
if ([string]::IsNullOrWhiteSpace($subscriptionId)) {
    throw 'Brak aktywnego logowania Azure CLI. Uruchom az login --use-device-code.'
}
Write-Host "Aktywna subskrypcja: $subscriptionId" -ForegroundColor Green

Write-Host "`n[1/6] Grupa zasobow..." -ForegroundColor Yellow
Invoke-AzChecked 'Tworzenie/sprawdzenie Resource Group' @(
    'group', 'create', '--name', $ResourceGroup, '--location', $Location, '-o', 'none'
)

# Rejestracja wymaganych dostawcow Azure
$requiredProviders = @(
    "Microsoft.Storage",
    "Microsoft.App",
    "Microsoft.OperationalInsights",
    "Microsoft.ContainerRegistry",
    "Microsoft.ManagedIdentity"
)

foreach ($provider in $requiredProviders) {

    Write-Host "Sprawdzanie: $provider" -ForegroundColor Cyan

    $status = az provider show `
        --namespace $provider `
        --query registrationState `
        -o tsv

    if ($LASTEXITCODE -ne 0) {
        throw "Nie mozna sprawdzic dostawcy $provider"
    }

    if ($status.Trim() -ne "Registered") {

        Write-Host "Rejestrowanie: $provider" -ForegroundColor Yellow

        az provider register `
            --namespace $provider `
            --wait

        if ($LASTEXITCODE -ne 0) {
            throw "Nie udalo sie zarejestrowac $provider"
        }
    }

    Write-Host "$provider - OK" -ForegroundColor Green
}

Write-Host '[2/6] Azure Storage Account...' -ForegroundColor Yellow
$existingStorage = Get-AzValue 'Sprawdzanie kont Storage' @(
    'storage', 'account', 'list', '--resource-group', $ResourceGroup,
    '--query', "[?name=='$StorageAccount'].name | [0]", '-o', 'tsv'
)
if ([string]::IsNullOrWhiteSpace($existingStorage)) {
    Invoke-AzChecked 'Tworzenie Storage Account' @(
        'storage', 'account', 'create',
        '--name', $StorageAccount,
        '--resource-group', $ResourceGroup,
        '--location', $Location,
        '--sku', 'Standard_LRS',
        '--kind', 'StorageV2',
        '--allow-blob-public-access', 'false',
        '-o', 'none'
    )
} else {
    Write-Host 'Konto Storage juz istnieje w tej grupie - wykorzystuje je.' -ForegroundColor Green
}

$storageId = Get-AzValue 'Pobieranie identyfikatora Storage Account' @(
    'storage', 'account', 'show', '--name', $StorageAccount,
    '--resource-group', $ResourceGroup, '--query', 'id', '-o', 'tsv'
)
if ([string]::IsNullOrWhiteSpace($storageId)) {
    throw 'Storage Account nie ma poprawnego identyfikatora zasobu.'
}

Write-Host '[3/6] Blob Container i Table Storage...' -ForegroundColor Yellow
Invoke-AzChecked 'Tworzenie kontenera Blob' @(
    'storage', 'container', 'create', '--name', $BlobContainerName,
    '--account-name', $StorageAccount, '--auth-mode', 'login', '-o', 'none'
)
Invoke-AzChecked 'Tworzenie tabeli audytu' @(
    'storage', 'table', 'create', '--name', $AuditTableName,
    '--account-name', $StorageAccount, '--auth-mode', 'login', '-o', 'none'
)

Write-Host '[4/6] Container Apps Environment...' -ForegroundColor Yellow
# Wykorzystujemy ARM zamiast filtrowac liste przez zapytanie JMESPath z nazwa.
# Zapobiega to przypadkowemu ponowieniu "env create" i utworzeniu kolejnego workspace.
$environmentNames = Get-AzValue 'Lista istniejacych srodowisk Container Apps' @(
    'resource', 'list', '--resource-group', $ResourceGroup,
    '--resource-type', 'Microsoft.App/managedEnvironments',
    '--query', '[].name', '-o', 'tsv'
)
$environmentExists = @($environmentNames -split "`r?`n") -contains $EnvironmentName
if (-not $environmentExists) {
    Write-Host "Brak srodowiska '$EnvironmentName' na liscie ARM. Tworzenie..." -ForegroundColor Yellow
    Invoke-AzChecked 'Tworzenie Container Apps Environment' @(
        'containerapp', 'env', 'create', '--name', $EnvironmentName,
        '--resource-group', $ResourceGroup, '--location', $Location, '-o', 'none'
    )
} else {
    Write-Host 'Container Apps Environment juz istnieje - wykorzystuje je.' -ForegroundColor Green
}

Write-Host '[5/6] Budowanie obrazu / wdrazanie aplikacji...' -ForegroundColor Yellow
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($DeployMode -eq 'source') {
    Invoke-AzChecked 'Budowanie i wdrazanie aplikacji ze zrodel' @(
        'containerapp', 'up',
        '--name', $ContainerAppName,
        '--resource-group', $ResourceGroup,
        '--location', $Location,
        '--environment', $EnvironmentName,
        '--source', $rootDir,
        '--target-port', '8080',
        '--ingress', 'external',
        '--env-vars',
            "BLOB_STORAGE_URI=https://$StorageAccount.blob.core.windows.net/$BlobContainerName",
            "TABLE_STORAGE_URI=https://$StorageAccount.table.core.windows.net/",
            "AUDIT_TABLE_NAME=$AuditTableName",
            "CLIENT_QUOTAS=$ClientQuotas",
            'TZ=Europe/Warsaw',
            'DOTNET_GCHeapHardLimit=0x30000000'
    )
} else {
    if ($DeployMode -eq 'build-image') {
        $existingAcr = az acr show --name $AcrName --resource-group $AcrResourceGroup --query name -o tsv 2>$null
        if ($LASTEXITCODE -ne 0) {
            Invoke-AzChecked 'Tworzenie Azure Container Registry' @(
            'acr', 'create', '--name', $AcrName, '--resource-group', $AcrResourceGroup,
                '--location', $Location, '--sku', 'Basic', '--admin-enabled', 'false', '-o', 'none'
            )
        }
        $Image = "$AcrName.azurecr.io/whitelist-api:$ImageTag"
        $imageRegistry = 'acr'
        Invoke-AzChecked 'Budowanie i publikowanie obrazu Docker w ACR' @(
            'acr', 'build', '--registry', $AcrName, '--image', "whitelist-api:$ImageTag", $rootDir
        )
    }

    if ($imageRegistry -eq 'acr') {
    $acrId = Get-AzValue 'Pobieranie identyfikatora ACR' @(
        'acr', 'show', '--name', $AcrName, '--resource-group', $AcrResourceGroup, '--query', 'id', '-o', 'tsv'
    )
    $acrServer = "$AcrName.azurecr.io"
    $pullIdentityName = "$ContainerAppName-acr-pull"
    $existingIdentity = az identity show --name $pullIdentityName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) {
        Invoke-AzChecked 'Tworzenie Managed Identity do pobierania obrazu' @(
            'identity', 'create', '--name', $pullIdentityName,
            '--resource-group', $ResourceGroup, '--location', $Location, '-o', 'none'
        )
    }
    $pullIdentityId = Get-AzValue 'Pobieranie Managed Identity ACR' @(
        'identity', 'show', '--name', $pullIdentityName, '--resource-group', $ResourceGroup, '--query', 'id', '-o', 'tsv'
    )
    $pullPrincipalId = Get-AzValue 'Pobieranie Principal ID ACR' @(
        'identity', 'show', '--name', $pullIdentityName, '--resource-group', $ResourceGroup, '--query', 'principalId', '-o', 'tsv'
    )
    $acrRoles = Get-AzValue 'Sprawdzanie roli AcrPull' @(
        'role', 'assignment', 'list', '--scope', $acrId,
        '--query', "[?principalId=='$pullPrincipalId'].roleDefinitionName", '-o', 'tsv'
    )
    if (@($acrRoles -split "`r?`n") -notcontains 'AcrPull') {
        Invoke-AzChecked 'Nadawanie roli AcrPull' @(
            'role', 'assignment', 'create',
            '--assignee-object-id', $pullPrincipalId,
            '--assignee-principal-type', 'ServicePrincipal',
            '--role', 'AcrPull', '--scope', $acrId, '-o', 'none'
        )
    }

    $existingApp = az containerapp show --name $ContainerAppName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingApp)) {
        Invoke-AzChecked 'Przypisanie tozsamosci do pobierania obrazu' @(
            'containerapp', 'identity', 'assign', '--name', $ContainerAppName,
            '--resource-group', $ResourceGroup, '--system-assigned',
            '--user-assigned', $pullIdentityId, '-o', 'none'
        )
        Invoke-AzChecked 'Konfiguracja rejestru obrazu' @(
            'containerapp', 'registry', 'set', '--name', $ContainerAppName,
            '--resource-group', $ResourceGroup, '--server', $acrServer, '--identity', $pullIdentityId
        )
        Invoke-AzChecked 'Aktualizacja aplikacji do obrazu z ACR' @(
            'containerapp', 'update', '--name', $ContainerAppName,
            '--resource-group', $ResourceGroup, '--image', $Image, '--set-env-vars',
            "BLOB_STORAGE_URI=https://$StorageAccount.blob.core.windows.net/$BlobContainerName",
            "TABLE_STORAGE_URI=https://$StorageAccount.table.core.windows.net/",
            "AUDIT_TABLE_NAME=$AuditTableName", "CLIENT_QUOTAS=$ClientQuotas",
            'TZ=Europe/Warsaw', 'DOTNET_GCHeapHardLimit=0x30000000'
        )
    } else {
        Invoke-AzChecked 'Utworzenie aplikacji z obrazem ACR' @(
            'containerapp', 'create', '--name', $ContainerAppName,
            '--resource-group', $ResourceGroup, '--environment', $EnvironmentName,
            '--image', $Image, '--target-port', '8080', '--ingress', 'external',
            '--system-assigned', '--user-assigned', $pullIdentityId,
            '--registry-server', $acrServer, '--registry-identity', $pullIdentityId,
            '--env-vars',
            "BLOB_STORAGE_URI=https://$StorageAccount.blob.core.windows.net/$BlobContainerName",
            "TABLE_STORAGE_URI=https://$StorageAccount.table.core.windows.net/",
            "AUDIT_TABLE_NAME=$AuditTableName", "CLIENT_QUOTAS=$ClientQuotas",
            'TZ=Europe/Warsaw', 'DOTNET_GCHeapHardLimit=0x30000000', '-o', 'none'
        )
    }
    } else {
        $dockerHubServer = 'index.docker.io'
        $registryArguments = @()
        if (-not [string]::IsNullOrWhiteSpace($DockerHubUsername)) {
            $registryArguments = @(
                '--registry-server', $dockerHubServer,
                '--registry-username', $DockerHubUsername,
                '--registry-password', $DockerHubToken
            )
        }

        $existingApp = az containerapp show --name $ContainerAppName --resource-group $ResourceGroup --query name -o tsv 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingApp)) {
            if (-not [string]::IsNullOrWhiteSpace($DockerHubUsername)) {
                Invoke-AzChecked 'Konfiguracja prywatnego repozytorium Docker Hub' @(
                    'containerapp', 'registry', 'set', '--name', $ContainerAppName,
                    '--resource-group', $ResourceGroup, '--server', $dockerHubServer,
                    '--username', $DockerHubUsername, '--password', $DockerHubToken
                )
            }
            Invoke-AzChecked 'Aktualizacja aplikacji do obrazu Docker Hub' @(
                'containerapp', 'update', '--name', $ContainerAppName,
                '--resource-group', $ResourceGroup, '--image', $Image, '--set-env-vars',
                "BLOB_STORAGE_URI=https://$StorageAccount.blob.core.windows.net/$BlobContainerName",
                "TABLE_STORAGE_URI=https://$StorageAccount.table.core.windows.net/",
                "AUDIT_TABLE_NAME=$AuditTableName", "CLIENT_QUOTAS=$ClientQuotas",
                'TZ=Europe/Warsaw', 'DOTNET_GCHeapHardLimit=0x30000000'
            )
        } else {
            $createArguments = @(
                'containerapp', 'create', '--name', $ContainerAppName,
                '--resource-group', $ResourceGroup, '--environment', $EnvironmentName,
                '--image', $Image, '--target-port', '8080', '--ingress', 'external',
                '--system-assigned'
            ) + $registryArguments + @(
                '--env-vars',
                "BLOB_STORAGE_URI=https://$StorageAccount.blob.core.windows.net/$BlobContainerName",
                "TABLE_STORAGE_URI=https://$StorageAccount.table.core.windows.net/",
                "AUDIT_TABLE_NAME=$AuditTableName", "CLIENT_QUOTAS=$ClientQuotas",
                'TZ=Europe/Warsaw', 'DOTNET_GCHeapHardLimit=0x30000000', '-o', 'none'
            )
            Invoke-AzChecked 'Utworzenie aplikacji z obrazem Docker Hub' $createArguments
        }
    }
    Write-Host "Uzywany obraz: $Image" -ForegroundColor Green
}

Write-Host '[6/6] Managed Identity i RBAC...' -ForegroundColor Yellow
# Podstawowa wersja "az containerapp up" nie obsluguje --system-assigned.
# Tozsamosc nadajemy dopiero po utworzeniu Container App.
Invoke-AzChecked 'Przypisanie system-assigned Managed Identity' @(
    'containerapp', 'identity', 'assign',
    '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup,
    '--system-assigned', '-o', 'none'
)

$principalId = Get-AzValue 'Odczyt Managed Identity' @(
    'containerapp', 'identity', 'show',
    '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup,
    '--query', 'principalId', '-o', 'tsv'
)
if ([string]::IsNullOrWhiteSpace($principalId) -or $principalId -eq 'null') {
    throw 'Container App nie ma Principal ID (Managed Identity).'
}
Write-Host "Principal ID: $principalId" -ForegroundColor Green

$roleNames = @('Storage Blob Data Contributor', 'Storage Table Data Contributor')
$roleLines = Get-AzValue 'Odczyt istniejacych przypisan RBAC' @(
    'role', 'assignment', 'list',
    '--scope', $storageId,
    '--query', "[?principalId=='$principalId'].roleDefinitionName",
    '-o', 'tsv'
)
$assignedRoles = @($roleLines -split "`r?`n" | ForEach-Object { $_.Trim() })
foreach ($role in $roleNames) {
    if ($assignedRoles -contains $role) {
        Write-Host "Rola juz przypisana: $role" -ForegroundColor Green
        continue
    }
    Invoke-AzChecked "Przypisanie roli $role" @(
        'role', 'assignment', 'create',
        '--assignee-object-id', $principalId,
        '--assignee-principal-type', 'ServicePrincipal',
        '--role', $role,
        '--scope', $storageId,
        '-o', 'none'
    )
}

# Aktualizacja limitow zasobow po przypisaniu uprawnien tworzy nowa rewizje.
# Samo nadanie RBAC moze wymagac czasu na propagacje.
Invoke-AzChecked 'Konfiguracja CPU, pamieci i liczby replik' @(
    'containerapp', 'update',
    '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup,
    '--cpu', '0.5',
    '--memory', '1.0Gi',
    '--min-replicas', '0',
    '--max-replicas', '2',
    '-o', 'none'
)

$fqdn = Get-AzValue 'Pobieranie adresu aplikacji' @(
    'containerapp', 'show',
    '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup,
    '--query', 'properties.configuration.ingress.fqdn', '-o', 'tsv'
)
if ([string]::IsNullOrWhiteSpace($fqdn)) {
    throw 'Wdrozenie zakonczone, ale brak FQDN aplikacji. Sprawdz konfiguracje ingress.'
}

Write-Host "`nWdrozenie zasobow zakonczone. Endpoint: https://$fqdn" -ForegroundColor Green
Write-Host "Sprawdzenie: Invoke-RestMethod -Uri https://$fqdn/healthz"
Write-Host "Przyklad API: https://$fqdn/k/bc/api/check/nip/5260250995/bank-account/02102010260000140201648043"
Write-Host 'Uwaga: RBAC moze wymagac czasu na propagacje. Sprawdz healthz i logi kontenera.' -ForegroundColor Yellow
