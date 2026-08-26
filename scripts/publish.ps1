# Publica JadeOneDesktop.exe (self-contained win-x64) y lo copia a storage para el endpoint de descarga.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "publish"
$storageDir = Join-Path (Split-Path -Parent $root) "api-app_crm\storage\app\desktop"

Write-Host "Publicando JadeOne Desktop ($Configuration, win-x64, self-contained)..."
dotnet publish (Join-Path $root "SaraBI.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir

$exe = Join-Path $outDir "JadeOneDesktop.exe"
if (-not (Test-Path $exe)) {
    throw "No se generó JadeOneDesktop.exe en $outDir"
}

New-Item -ItemType Directory -Force -Path $storageDir | Out-Null
Copy-Item $exe (Join-Path $storageDir "JadeOneDesktop.exe") -Force

Write-Host "Listo:"
Write-Host "  $exe"
Write-Host "  Copiado a $storageDir\JadeOneDesktop.exe para GET /api/fabric/viewer/desktop/download"
Write-Host "Instale en el PC con: .\scripts\install.ps1"
