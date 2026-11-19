# Claude Code "Notification" hook installed by the Claude Usage widget.
# Appends one line per notification (permission request, question...) to
# events.jsonl, which the widget reads to alert that a session needs you.
$ErrorActionPreference = 'SilentlyContinue'

try {
    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    $dir = Join-Path $claudeDir 'usage-widget'
    $utf8 = New-Object System.Text.UTF8Encoding $false

    $stdin = New-Object System.IO.StreamReader ([Console]::OpenStandardInput()), $utf8
    $data = $stdin.ReadToEnd() | ConvertFrom-Json

    if ($data.session_id) {
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $file = Join-Path $dir 'events.jsonl'
        # Keep the file small; the widget notices the rotation.
        if ((Test-Path -LiteralPath $file) -and (Get-Item -LiteralPath $file).Length -gt 200KB) {
            Remove-Item -LiteralPath $file -Force
        }
        $line = [ordered]@{
            ts         = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            event      = [string]$data.hook_event_name
            session_id = [string]$data.session_id
            type       = [string]$data.notification_type
            message    = [string]$data.message
        } | ConvertTo-Json -Compress
        [IO.File]::AppendAllText($file, $line + "`n", $utf8)
    }
} catch { }

# Never block or fail the session because of the widget.
exit 0
