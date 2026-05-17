param(
    [string]$ConfigPath = ".\\scripts\\deploy.azure.json"
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing required command: $Name"
    }
}

function Get-ConfigValue {
    param(
        [object]$Config,
        [string]$Name
    )

    $value = $Config.$Name
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        throw "Missing required config value: $Name"
    }

    return [string]$value
}

Require-Command dotnet
Require-Command az
Require-Command npm.cmd
Require-Command tar.exe

$configFullPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $ConfigPath))
if (-not (Test-Path $configFullPath)) {
    throw "Config file not found: $configFullPath. Copy scripts/deploy.azure.example.json to scripts/deploy.azure.json and fill it in."
}

$repoRoot = [System.IO.Path]::GetFullPath((Get-Location).Path)
$config = Get-Content $configFullPath -Raw | ConvertFrom-Json

$subscriptionId = [string]$config.subscriptionId
$resourceGroup = Get-ConfigValue $config "resourceGroup"
$location = Get-ConfigValue $config "location"
$planName = Get-ConfigValue $config "planName"
$appName = Get-ConfigValue $config "appName"
$sku = if ([string]::IsNullOrWhiteSpace([string]$config.sku)) { "F1" } else { [string]$config.sku }
$runtime = if ([string]::IsNullOrWhiteSpace([string]$config.runtime)) { "DOTNETCORE:10.0" } else { [string]$config.runtime }
$projectPath = if ([string]::IsNullOrWhiteSpace([string]$config.projectPath)) { "PolishSpotPriceToLoxone/PolishSpotPriceToLoxone.csproj" } else { [string]$config.projectPath }
$projectFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $projectPath))

if (-not (Test-Path $projectFullPath)) {
    throw "Project file not found: $projectFullPath"
}

if (-not [string]::IsNullOrWhiteSpace($subscriptionId)) {
    az account set --subscription $subscriptionId | Out-Null
}

$deployRoot = Join-Path $repoRoot ".deploy"
$publishDir = Join-Path $deployRoot "publish"
$zipPath = Join-Path $deployRoot "publish.zip"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "Ensuring resource group..."
$rgExists = az group exists --name $resourceGroup | ConvertFrom-Json
if (-not $rgExists) {
    az group create --name $resourceGroup --location $location | Out-Null
}

Write-Host "Ensuring App Service plan..."
$planId = az appservice plan show --resource-group $resourceGroup --name $planName --query id -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($planId)) {
    az appservice plan create --name $planName --resource-group $resourceGroup --location $location --sku $sku --is-linux | Out-Null
}

Write-Host "Ensuring Web App..."
$appId = az webapp show --resource-group $resourceGroup --name $appName --query id -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($appId)) {
    az webapp create --resource-group $resourceGroup --plan $planName --name $appName --runtime $runtime | Out-Null
}

az webapp update --resource-group $resourceGroup --name $appName --https-only true | Out-Null

$appSettingsArgs = @()
if ($config.appSettings) {
    foreach ($property in $config.appSettings.PSObject.Properties) {
        if (-not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            $appSettingsArgs += "$($property.Name)=$($property.Value)"
        }
    }
}

if ($appSettingsArgs.Count -gt 0) {
    Write-Host "Updating app settings..."
    az webapp config appsettings set --resource-group $resourceGroup --name $appName --settings $appSettingsArgs | Out-Null
}

$pricesSqlConnection = ""
if ($config.connectionStrings -and $config.connectionStrings.PricesSql) {
    $pricesSqlConnection = [string]$config.connectionStrings.PricesSql
}

if (-not [string]::IsNullOrWhiteSpace($pricesSqlConnection)) {
    Write-Host "Updating SQL connection string..."
    az webapp config connection-string set --resource-group $resourceGroup --name $appName --connection-string-type SQLAzure --settings PricesSql=$pricesSqlConnection | Out-Null
}

Write-Host "Publishing application..."
dotnet publish $projectFullPath -c Release -o $publishDir

Write-Host "Packing deployment zip..."
tar.exe -a -cf $zipPath -C $publishDir .

Write-Host "Deploying to Azure Web App..."
az webapp deploy --resource-group $resourceGroup --name $appName --src-path $zipPath --type zip --clean true --restart true | Out-Null

$hostName = az webapp show --resource-group $resourceGroup --name $appName --query defaultHostName -o tsv

Write-Host ""
Write-Host "Deployment complete."
Write-Host "App URL: https://$hostName/"
Write-Host "Docs URL: https://$hostName/loxone/docs"
Write-Host "Loxone URL: https://$hostName/loxone/prices"
