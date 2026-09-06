# AgentReaper elevated-task installer (ASCII only - PowerShell 5.1 reads BOM-less files as ANSI)
#
# Registers a Task Scheduler task that runs "AgentReaper.exe --memory-commands"
# with "Run with highest privileges" (RunLevel = Highest) and NO trigger (on-demand only).
# The tray app starts it with `schtasks /run`, so the memory-list operations run elevated
# WITHOUT showing a UAC prompt every time. UAC is required ONCE, here, to register the task.
#
#   .\install-elevated-task.ps1                    register .\dist\AgentReaper.exe
#   .\install-elevated-task.ps1 -ExePath <path>    register a specific exe
#   .\install-elevated-task.ps1 -Uninstall         remove
#
# Security note: this grants a specific, fixed command line elevated on-demand execution.
# It does not accept arguments from the caller - the action is hard-coded to --memory-commands.
# That mode only calls NtSetSystemInformation(SystemMemoryListInformation); it never
# terminates a process. Read src/Reaper.cs RunMemoryCommands before you approve the UAC prompt.
#
# This step is OPTIONAL. Without it, everything still works; the memory-list operations
# simply ask for UAC each time (or are skipped).

param([switch]$Uninstall, [string]$ExePath)

$ErrorActionPreference = 'Stop'

$TaskName = 'AgentReaper-MemoryCommands'

if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot 'dist\AgentReaper.exe' }
if (Test-Path $ExePath) { $ExePath = (Resolve-Path $ExePath).Path }
$exe = $ExePath

# Re-launch elevated if needed (single UAC prompt, only for this registration)
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
             [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
  Write-Host "Elevation required. Approve the UAC prompt (this is a one-time setup)."
  $psArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath, '-ExePath', $exe)
  if ($Uninstall) { $psArgs += '-Uninstall' }
  $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $psArgs -Verb RunAs -PassThru -Wait
  Write-Host ("Elevated run finished with exit code {0}" -f $p.ExitCode)
  return
}

if ($Uninstall) {
  $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($existing) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed task: $TaskName"
  } else {
    Write-Host "Task not registered: $TaskName"
  }
  return
}

if (-not (Test-Path $exe)) { throw "AgentReaper.exe not found. Run build.ps1 first: $exe" }

$action = New-ScheduledTaskAction -Execute $exe -Argument '--memory-commands' `
            -WorkingDirectory (Split-Path $exe -Parent)

# Interactive logon type keeps it running as the signed-in user, but elevated.
$principal = New-ScheduledTaskPrincipal -UserId ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME) `
               -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
              -AllowStartIfOnBatteries `
              -DontStopIfGoingOnBatteries `
              -StartWhenAvailable `
              -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
              -MultipleInstances IgnoreNew

# No -Trigger: the task never fires on its own, it only runs when explicitly started.
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal `
  -Settings $settings -Description 'AgentReaper: run memory list operations elevated without a UAC prompt' `
  -Force | Out-Null

Write-Host "Registered task: $TaskName"
Write-Host ("  Execute   : {0} --memory-commands" -f $exe)
Write-Host "  RunLevel  : Highest"
Write-Host "  Trigger   : none (on-demand only)"
Write-Host ""
Write-Host 'From now on, the tray "make it lighter now" action runs the memory operations'
Write-Host 'without a UAC prompt.'
