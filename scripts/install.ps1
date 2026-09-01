# Copia JadeOneDesktop.exe a %LocalAppData%\JadeOneDesktop y registra jadeone-desktop://
param(
    [string]$SourceExe = ""
)

$ErrorActionPreference = "Stop"
$destDir = Join-Path $env:LOCALAPPDATA "JadeOneDesktop"
$destExe = Join-Path $destDir "JadeOneDesktop.exe"

if (-not $SourceExe) {
    $published = Join-Path (Split-Path -Parent $PSScriptRoot) "publish\JadeOneDesktop.exe"
    if (Test-Path $published) {
        $SourceExe = $published
    } elseif ($PSCommandPath) {
        $beside = Join-Path (Split-Path -Parent $PSScriptRoot) "JadeOneDesktop.exe"
        if (Test-Path $beside) { $SourceExe = $beside }
    }
}

if (-not $SourceExe -or -not (Test-Path $SourceExe)) {
    throw "No se encontró JadeOneDesktop.exe. Ejecute primero .\scripts\publish.ps1"
}

New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item $SourceExe $destExe -Force

$legacy = "HKCU:\Software\Classes\sarabi"
if (Test-Path $legacy) {
    Remove-Item -Path $legacy -Recurse -Force
}

$classes = "HKCU:\Software\Classes\jadeone-desktop"
New-Item -Path $classes -Force | Out-Null
Set-ItemProperty -Path $classes -Name "(default)" -Value "URL:JadeOne Desktop Protocol"
New-ItemProperty -Path $classes -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null

$icon = Join-Path $classes "DefaultIcon"
New-Item -Path $icon -Force | Out-Null
Set-ItemProperty -Path $icon -Name "(default)" -Value "`"$destExe`",0"

$cmd = Join-Path $classes "shell\open\command"
New-Item -Path $cmd -Force | Out-Null
Set-ItemProperty -Path $cmd -Name "(default)" -Value "`"$destExe`" `"%1`""

$programs = [Environment]::GetFolderPath("Programs")
$oldShortcut = Join-Path $programs "Sara BI Escritorio.lnk"
if (Test-Path $oldShortcut) {
    Remove-Item $oldShortcut -Force
}

$shortcutPath = Join-Path $programs "JadeOne Desktop.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $destExe
$shortcut.WorkingDirectory = $destDir
$shortcut.Description = "JadeOne Desktop"
$shortcut.IconLocation = "$destExe,0"
$shortcut.Save()

Write-Host "Instalado en $destExe"
Write-Host "Protocolo jadeone-desktop:// registrado."
Write-Host "Acceso directo: $shortcutPath"
Write-Host "Desde el listado de vistas, use el botón de escritorio."
