<#
.SYNOPSIS
  Installs (or removes) the Claude Usage widget for the current user.

.DESCRIPTION
  Copies ClaudeUsageWidget.exe to a fixed folder, hooks the Claude Code
  statusline so the widget receives the rate limits, and starts the widget.
  Run from the folder that holds this script, the exe and feed.ps1:

    powershell -ExecutionPolicy Bypass -File .\install.ps1 [-AutoStart]
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall

  An existing statusline is kept: the widget's feed runs first and then calls it.
  No admin rights needed. Claude Code must be installed and logged in separately.
#>
[CmdletBinding()]
param(
    # Start the widget with Windows.
    [switch]$AutoStart,
    # Remove the widget and put the previous statusline back.
    [switch]$Uninstall,
    # Don't start the widget after installing.
    [switch]$NoLaunch,
    # Only (re)install the Notification hook; leave the exe and statusline alone.
    [switch]$HooksOnly,
    # Don't test whether the widget can read the limits (one short message on Haiku).
    [switch]$NoCheck,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsageWidget')
)

$ErrorActionPreference = 'Stop'

$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
$widgetDir = Join-Path $claudeDir 'usage-widget'
$settingsPath = Join-Path $claudeDir 'settings.json'
$feedPath = Join-Path $widgetDir 'feed.ps1'
$wrappedPath = Join-Path $widgetDir 'wrapped-command.txt'
$exeTarget = Join-Path $InstallDir 'ClaudeUsageWidget.exe'
$feedCommand = "powershell -ExecutionPolicy Bypass -NoProfile -File `"$feedPath`""
$notifyPath = Join-Path $widgetDir 'notify.ps1'
$notifyCommand = "powershell -ExecutionPolicy Bypass -NoProfile -File `"$notifyPath`""
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'ClaudeUsageWidget'
$utf8 = New-Object System.Text.UTF8Encoding $false

function Step([string]$text) { Write-Host "  $text" }

function Read-Settings {
    if (-not (Test-Path -LiteralPath $settingsPath)) { return [pscustomobject]@{} }
    $raw = [IO.File]::ReadAllText($settingsPath)
    if ([string]::IsNullOrWhiteSpace($raw)) { return [pscustomobject]@{} }
    try { return $raw | ConvertFrom-Json }
    catch { throw "settings.json não é JSON válido; não foi alterado: $settingsPath" }
}

function Save-Settings($settings) {
    if (Test-Path -LiteralPath $settingsPath) {
        $backup = "$settingsPath.bak-usage-widget-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item -LiteralPath $settingsPath -Destination $backup
        Step "Cópia de segurança: $backup"
    } elseif (-not (Test-Path -LiteralPath $claudeDir)) {
        New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null
    }
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 100), $utf8)
}

function Test-IsFeed([string]$command) { $command -and $command -like '*usage-widget*feed.ps1*' }

# True when the configured statusline script already writes the widget's feed
# itself (the case on the machine where the widget was built).
function Test-HasOwnFeed([string]$command) {
    if ($command -match '"([^"]+\.ps1)"' -or $command -match '(\S+\.ps1)') {
        $script = $Matches[1]
        if (Test-Path -LiteralPath $script) {
            return [bool](Select-String -LiteralPath $script -Pattern 'usage-widget' -SimpleMatch -Quiet)
        }
    }
    return $false
}

function Test-IsNotifyEntry($entry) {
    [bool](@($entry.hooks) | Where-Object { $_.command -like '*usage-widget*notify.ps1*' })
}

# Adds the Notification hook that tells the widget a session needs input.
# Other hooks are left as they are. Returns $true when settings changed.
function Add-NotifyHook($settings) {
    if ($null -eq $settings.hooks) {
        $settings | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $entries = @()
    if ($null -ne $settings.hooks.Notification) { $entries = @($settings.hooks.Notification) }
    if ($entries | Where-Object { Test-IsNotifyEntry $_ }) { return $false }

    $entries += [pscustomobject]@{
        hooks = @([pscustomobject]@{ type = 'command'; command = $notifyCommand })
    }
    $settings.hooks | Add-Member -NotePropertyName Notification -NotePropertyValue $entries -Force
    return $true
}

function Remove-NotifyHook($settings) {
    if ($null -eq $settings.hooks -or $null -eq $settings.hooks.Notification) { return $false }
    $entries = @($settings.hooks.Notification)
    $kept = @($entries | Where-Object { -not (Test-IsNotifyEntry $_) })
    if ($kept.Count -eq $entries.Count) { return $false }

    if ($kept.Count -gt 0) { $settings.hooks.Notification = $kept }
    else { $settings.hooks.PSObject.Properties.Remove('Notification') }
    if (@($settings.hooks.PSObject.Properties).Count -eq 0) { $settings.PSObject.Properties.Remove('hooks') }
    return $true
}

function Install-NotifyHook($settings) {
    $source = Join-Path $PSScriptRoot 'notify.ps1'
    if (-not (Test-Path -LiteralPath $source)) { throw "Falta o ficheiro ao lado do install.ps1: $source" }
    New-Item -ItemType Directory -Path $widgetDir -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $notifyPath -Force
    if (Add-NotifyHook $settings) {
        Step 'Hook de avisos: instalado (vale para sessões abertas a partir de agora).'
        return $true
    }
    Step 'Hook de avisos: já instalado (script atualizado).'
    return $false
}

function Stop-Widget {
    Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exeTarget } |
        ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit(5000) | Out-Null }
}

# ---- uninstall ---------------------------------------------------------------

if ($Uninstall) {
    Write-Host 'A remover o Claude Usage widget'
    Stop-Widget

    $settings = Read-Settings
    $changed = $false
    if (Test-IsFeed $settings.statusLine.command) {
        if (Test-Path -LiteralPath $wrappedPath) {
            $settings.statusLine.command = ([IO.File]::ReadAllText($wrappedPath)).Trim()
            Step 'Statusline anterior reposto.'
        } else {
            $settings.PSObject.Properties.Remove('statusLine')
            Step 'Statusline do widget removido.'
        }
        $changed = $true
    }
    if (Remove-NotifyHook $settings) {
        Step 'Hook de avisos removido.'
        $changed = $true
    }
    if ($changed) { Save-Settings $settings }

    Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    if (Test-Path -LiteralPath $widgetDir) { Remove-Item -LiteralPath $widgetDir -Recurse -Force }
    Write-Host 'Removido.'
    return
}

# ---- install -----------------------------------------------------------------

if ($HooksOnly) {
    Write-Host 'A instalar o hook de avisos do Claude Usage widget'
    $settings = Read-Settings
    if (Install-NotifyHook $settings) { Save-Settings $settings }
    return
}

Write-Host 'A instalar o Claude Usage widget'

$exeSource = Join-Path $PSScriptRoot 'ClaudeUsageWidget.exe'
$feedSource = Join-Path $PSScriptRoot 'feed.ps1'
foreach ($f in $exeSource, $feedSource) {
    if (-not (Test-Path -LiteralPath $f)) { throw "Falta o ficheiro ao lado do install.ps1: $f" }
}

$claude = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claude -and -not (Test-Path -LiteralPath (Join-Path $HOME '.local\bin\claude.exe'))) {
    Write-Warning 'Claude Code não encontrado. Instala-o e faz /login; sem ele o widget não recebe dados.'
}

# 1. The exe.
Stop-Widget
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -LiteralPath $exeSource -Destination $exeTarget -Force
Unblock-File -LiteralPath $exeTarget -ErrorAction SilentlyContinue
Step "Widget: $exeTarget"

# 2. The statusline feed.
New-Item -ItemType Directory -Path $widgetDir -Force | Out-Null
$settings = Read-Settings
$current = [string]$settings.statusLine.command
$changed = $false

if (Test-IsFeed $current) {
    Copy-Item -LiteralPath $feedSource -Destination $feedPath -Force
    Step 'Statusline: já ligado ao widget (feed atualizado).'
} elseif ($current -and (Test-HasOwnFeed $current)) {
    Step 'Statusline: o script existente já alimenta o widget; não foi alterado.'
} else {
    Copy-Item -LiteralPath $feedSource -Destination $feedPath -Force
    if ($current) {
        [IO.File]::WriteAllText($wrappedPath, $current, $utf8)
        $settings.statusLine.command = $feedCommand
        Step 'Statusline: o existente foi mantido e passa a ser chamado pelo feed do widget.'
    } else {
        $settings | Add-Member -NotePropertyName statusLine -Force -NotePropertyValue (
            [pscustomobject]@{ type = 'command'; command = $feedCommand })
        Step 'Statusline: instalado.'
    }
    $changed = $true
}

# 2b. The hook that reports "this session needs you".
if (Install-NotifyHook $settings) { $changed = $true }
if ($changed) { Save-Settings $settings }

# 3. Start with Windows.
if ($AutoStart) {
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name $runName -Value "`"$exeTarget`""
    Step 'Arranque com o Windows: ligado.'
}

# 4. Can the widget read the limits? It sends one short message through any
#    Claude Code it finds (CLI, desktop app, VS Code extension). Done before the
#    launch so the widget starts with the reading.
$limitsOk = $null
if (-not $NoCheck) {
    Write-Host '  A testar a leitura dos limites (envia um "hello" curto no Haiku; até 2 min)' -NoNewline
    $logPath = Join-Path $widgetDir 'widget.log'
    $logBefore = if (Test-Path -LiteralPath $logPath) { (Get-Item -LiteralPath $logPath).Length } else { 0 }
    $p = Start-Process -FilePath $exeTarget -ArgumentList '--hello' -WorkingDirectory $InstallDir -PassThru -WindowStyle Hidden
    $null = $p.Handle   # without holding the handle, ExitCode comes back empty in PowerShell 5.1
    # The widget gives up on its own after 90 s; this is the backstop. A dot
    # every few seconds, so a slow test doesn't look like a frozen console.
    $deadline = (Get-Date).AddSeconds(150)
    while (-not $p.WaitForExit(3000)) {
        Write-Host '.' -NoNewline
        if ((Get-Date) -gt $deadline) {
            # /T: the claude.exe it started too, or it keeps running hidden.
            & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null
            break
        }
    }
    Write-Host ''
    $limitsOk = $p.HasExited -and $p.ExitCode -eq 0

    # What the widget logged about it (which Claude Code it used, or why it failed).
    $logLine = ''
    if (Test-Path -LiteralPath $logPath) {
        $fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
        try {
            if ($fs.Length -lt $logBefore) { $logBefore = 0 }   # the log rotated
            $fs.Seek($logBefore, 'Begin') | Out-Null
            $lines = (New-Object IO.StreamReader($fs, $utf8)).ReadToEnd() -split "`r?`n" | Where-Object { $_ -match 'Hello' }
            if ($lines) { $logLine = ($lines | Select-Object -Last 1) -replace '^\S+ \S+ ', '' }
            if ($logLine.Length -gt 400) { $logLine = $logLine.Substring(0, 400) + '…' }
        } finally { $fs.Dispose() }
    }

    if ($limitsOk) {
        Step "Limites: OK. $logLine"
    } else {
        Write-Warning ("Os limites (5h / 7d) não vão aparecer. As sessões e os avisos funcionam na mesma.`n" +
            "  Para os limites: instala o Claude Code CLI, corre 'claude' num terminal`n" +
            "  e faz /login com a tua conta. Depois: botão direito no widget > Atualizar agora.")
        if ($logLine) { Step "Detalhe: $logLine" }
    }
}

# 5. Go.
if (-not $NoLaunch) {
    # Otherwise the widget keeps the folder this script ran from in use, and
    # that folder can't be deleted or rebuilt (e.g. dist\ by pack.ps1).
    Start-Process -FilePath $exeTarget -WorkingDirectory $InstallDir
    Step 'Widget iniciado (canto inferior direito).'
}

if ($limitsOk -ne $true) {
    Write-Host 'Instalado. Os limites aparecem depois da primeira resposta de uma sessão do Claude Code no terminal.'
} else {
    Write-Host 'Instalado.'
}
Write-Host 'Auto-hello e outras opções: botão direito no widget > Definições.'
