# AgentReaper autostart installer (ASCII only - PowerShell 5.1 reads BOM-less files as ANSI)
#
# Puts a shortcut in the current user's Startup folder. No admin rights needed.
#
#   .\install-autostart.ps1                    register .\dist\AgentReaper.exe
#   .\install-autostart.ps1 -ExePath <path>    register a specific exe
#   .\install-autostart.ps1 -Uninstall         remove

param([switch]$Uninstall, [string]$ExePath)

$ErrorActionPreference = 'Stop'

if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot 'dist\AgentReaper.exe' }

$startup = [Environment]::GetFolderPath('Startup')
$link = Join-Path $startup 'AgentReaper.lnk'

if ($Uninstall) {
  if (Test-Path $link) {
    Remove-Item $link -Force
    Write-Host "Removed: $link"
  } else {
    Write-Host "Not registered: $link"
  }
  return
}

if (-not (Test-Path $ExePath)) { throw "AgentReaper.exe not found. Run build.ps1 first: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($link)
$sc.TargetPath = $ExePath
$sc.WorkingDirectory = Split-Path $ExePath -Parent
$sc.Description = 'Reap leaked child processes left behind by AI coding agents'
$sc.WindowStyle = 7
$sc.Save()

Write-Host "Registered: $link"
Write-Host "Target    : $ExePath"
Write-Host ""
Write-Host "AgentReaper will start automatically at next sign-in."
