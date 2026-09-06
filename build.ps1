# AgentReaper build script (ASCII only - PowerShell 5.1 reads BOM-less files as ANSI)
#
# No .NET SDK required. Uses the .NET Framework 4.8 compiler that ships with Windows,
# so there is nothing to install and nothing to trust beyond this repository.
#
# /codepage:65001 makes csc read the BOM-less UTF-8 sources correctly.
#
#   .\build.ps1                 build into .\dist
#   .\build.ps1 -Dist <path>    build somewhere else

param([string]$Dist)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'src'

# Output goes to .\dist by default. It is gitignored: the exe is rebuilt often and
# agent-reaper.log is appended on every scan.
if (-not $Dist) { $Dist = Join-Path $root 'dist' }

$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'

if (-not (Test-Path $csc)) {
  throw "csc.exe not found: $csc  (needs .NET Framework 4.x, present on every supported Windows)"
}
if (-not (Test-Path $Dist)) { New-Item -ItemType Directory -Path $Dist -Force | Out-Null }

$out = Join-Path $Dist 'AgentReaper.exe'

$refs = @(
  'System.dll'
  'System.Core.dll'
  'System.Drawing.dll'
  'System.Windows.Forms.dll'
  'System.Management.dll'
) | ForEach-Object { '/reference:' + (Join-Path $fw $_) }

$sources = Get-ChildItem -Path $src -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
  '/nologo'
  '/target:winexe'
  '/platform:x64'
  '/optimize+'
  '/codepage:65001'
  ('/out:' + $out)
  ('/win32manifest:' + (Join-Path $src 'app.manifest'))
) + $refs + $sources

Write-Host "Building: $out"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

# Deploy config files next to the exe. Never overwrite an existing one:
# signatures.conf and settings.conf belong to the user, not to the build.
foreach ($f in @('signatures.conf', 'settings.conf')) {
  $srcFile = Join-Path $root $f
  $dstFile = Join-Path $Dist $f
  if (-not (Test-Path $dstFile)) { Copy-Item $srcFile $dstFile }
}

$fi = Get-Item $out
Write-Host ("Done: {0} ({1:N0} bytes)" -f $fi.FullName, $fi.Length)
Write-Host ""
Write-Host "Next: hand AGENTS.md to your coding agent, then run"
Write-Host ("  {0} --diagnose --json" -f $fi.FullName)
