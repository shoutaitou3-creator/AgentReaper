# ASCII only. Compile outside the live installation and use deterministic fixtures.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$fw = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$testOutput = Join-Path $env:TEMP ('agent-reaper-safety-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll') |
    ForEach-Object { '/reference:' + (Join-Path $fw $_) }
$common = @('/nologo', '/platform:x64', '/optimize+', '/codepage:65001') + $refs
$sources = Get-ChildItem -LiteralPath $src -Filter '*.cs' | ForEach-Object { $_.FullName }
$production = Join-Path $testOutput 'AgentReaper.exe'
& $csc ($common + @('/target:winexe', ('/out:' + $production), ('/win32manifest:' + (Join-Path $src 'app.manifest'))) + $sources)
if ($LASTEXITCODE -ne 0) { throw 'Production compile failed' }
$tests = Join-Path $testOutput 'ReaperSafetyTests.exe'
# Guard.cs is compiled in unchanged: the safety limits under test are the real ones.
& $csc ($common + @('/target:exe', ('/out:' + $tests), (Join-Path $src 'Reaper.cs'), (Join-Path $src 'Config.cs'), (Join-Path $src 'Guard.cs'), (Join-Path $PSScriptRoot 'ReaperSafetyTests.cs')))
if ($LASTEXITCODE -ne 0) { throw 'Safety test compile failed' }
& $tests
if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed' }
Write-Output ('Production build (not started): ' + $production)
Write-Output ('Production SHA256: ' + (Get-FileHash -LiteralPath $production -Algorithm SHA256).Hash)
