<#
.SYNOPSIS
  Installs (or removes) the Claude Usage widget.

.DESCRIPTION
  The widget installs itself now: a double click on ClaudeUsageWidget.exe in
  the unpacked zip is all it takes, and nothing else needs PowerShell. This
  script only hands over to it, for the old instructions and for widgets
  1.2 to 1.7, whose updates run it with -NoCheck:

    powershell -ExecutionPolicy Bypass -File .\install.ps1 [-AutoStart]
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Start the widget with Windows (for -NoCheck; the window asks).
    [switch]$AutoStart,
    # Remove the widget and put the previous statusline back.
    [switch]$Uninstall,
    # Don't start the widget after installing.
    [switch]$NoLaunch,
    # Kept for old command lines: an install always sets the hook too.
    [switch]$HooksOnly,
    # No window and no hello test: what a widget's own update asks for.
    [switch]$NoCheck,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsageWidget')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'ClaudeUsageWidget.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Falta o ClaudeUsageWidget.exe ao lado do install.ps1: $exe" }

# A downloaded zip marks its files as from the internet; started from a script,
# Windows may refuse them.
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.exe' | Unblock-File -ErrorAction SilentlyContinue

# Start-Process joins the arguments with spaces: the folder needs its quotes.
# One flat list of strings: Start-Process refuses a list inside a list.
$verb = if ($Uninstall) { '--uninstall' } else { '--install' }
$arguments = @($verb, '--dir', "`"$InstallDir`"")
if (-not $Uninstall -and $NoCheck) {
    $autoStartFlag = if ($AutoStart) { '--autostart' } else { '--no-autostart' }
    $arguments += @('--silent', '--no-check', $autoStartFlag)
    if ($NoLaunch) { $arguments += '--no-launch' }
}

Write-Host $(if ($Uninstall) { 'A remover o Claude Usage widget' } else { 'A instalar o Claude Usage widget' })
$p = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -PassThru
$null = $p.Handle   # without holding the handle, ExitCode comes back empty in PowerShell 5.1
$p.WaitForExit()

# A silent install writes its steps to install.log: show them here (an update keeps this output).
$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
$log = Join-Path $claudeDir 'usage-widget\install.log'
if (-not $Uninstall -and $NoCheck -and (Test-Path -LiteralPath $log)) {
    Get-Content -LiteralPath $log -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
}
exit $p.ExitCode
