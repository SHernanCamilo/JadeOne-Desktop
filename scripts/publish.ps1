# Publica JadeOneDesktop.exe (self-contained win-x64) y lo copia a storage para el endpoint de descarga.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "publish"
$storageDir = Join-Path (Split-Path -Parent $root) "api-appCertec\storage\app\desktop"

Write-Host "Publicando JadeOne Desktop ($Configuration, win-x64, self-contained)..."
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

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

# version.json = fuente de verdad para el endpoint /desktop/version (auto-update).
# La versión se lee directo del .exe publicado (asignada por <Version> en el .csproj).
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if (-not $fileVersion) {
    $fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
}
# Normalizar a Major.Minor.Build (descarta revision si viene 1.1.0.0)
$parts = ($fileVersion -split '\.')
if ($parts.Count -ge 3) {
    $version = "$($parts[0]).$($parts[1]).$($parts[2])"
} else {
    $version = $fileVersion
}

$versionMeta = @{
    version      = $version
    published_at = (Get-Date).ToString("o")
} | ConvertTo-Json
Set-Content -Path (Join-Path $storageDir "version.json") -Value $versionMeta -Encoding UTF8

Write-Host "Listo:"
Write-Host "  $exe"
Write-Host "  Version publicada: $version"
Write-Host "  Copiado a $storageDir\JadeOneDesktop.exe para GET /api/fabric/viewer/desktop/download"
Write-Host "  version.json escrito para GET /api/fabric/viewer/desktop/version"
Write-Host "Instale en el PC con: .\scripts\install.ps1"
