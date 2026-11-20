# Claude Code statusline command installed by the Claude Usage widget.
# Records the rate limits for the widget, then either hands over to the
# statusline that was configured before (wrapped-command.txt) or prints a
# minimal line of its own.
$ErrorActionPreference = 'SilentlyContinue'

$claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
$wDir = Join-Path $claudeDir 'usage-widget'
$utf8 = New-Object System.Text.UTF8Encoding $false

$raw = ''
$data = $null
try {
    $stdin = New-Object System.IO.StreamReader ([Console]::OpenStandardInput()), $utf8
    $raw = $stdin.ReadToEnd()
    if (-not [string]::IsNullOrWhiteSpace($raw)) { $data = $raw | ConvertFrom-Json }
} catch { }

# Several sessions write here and an idle one can carry stale numbers, so per
# window: a later resets_at wins, and within the same window the higher usage wins.
if ($null -ne $data.rate_limits) {
    try {
        if (-not (Test-Path -LiteralPath $wDir)) { New-Item -ItemType Directory -Path $wDir -Force | Out-Null }
        $wFile = Join-Path $wDir 'limits.json'
        $old = $null
        if (Test-Path -LiteralPath $wFile) {
            try { $old = Get-Content -LiteralPath $wFile -Raw | ConvertFrom-Json } catch { }
        }
        $out = [ordered]@{ updated_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
        foreach ($k in 'five_hour', 'seven_day') {
            $n = $data.rate_limits.$k
            $o = if ($old) { $old.$k } else { $null }
            $pick = $o
            if ($null -ne $n -and $null -ne $n.resets_at) {
                if ($null -eq $o -or $null -eq $o.resets_at) { $pick = $n }
                else {
                    $diff = [long]$n.resets_at - [long]$o.resets_at
                    if ($diff -gt 60) { $pick = $n }
                    elseif ($diff -ge -60 -and [double]$n.used_percentage -ge [double]$o.used_percentage) { $pick = $n }
                }
            }
            if ($null -ne $pick) {
                $out[$k] = [ordered]@{ used_percentage = [double]$pick.used_percentage; resets_at = [long]$pick.resets_at }
            }
        }
        $tmp = "$wFile.$PID.tmp"
        try {
            [IO.File]::WriteAllText($tmp, ($out | ConvertTo-Json -Depth 4))
            Move-Item -LiteralPath $tmp -Destination $wFile -Force
        } finally {
            # Two sessions writing at the same moment: one move loses. The next
            # statusline refresh writes again, so just don't leave the file behind.
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
        }
    } catch { }
}

$stdout = [Console]::OpenStandardOutput()
function Write-Out([string]$text) {
    $bytes = $utf8.GetBytes($text)
    $stdout.Write($bytes, 0, $bytes.Length)
    $stdout.Flush()
}

# Hand over to the statusline that was there before the widget was installed.
$wrappedFile = Join-Path $wDir 'wrapped-command.txt'
if (Test-Path -LiteralPath $wrappedFile) {
    $wrapped = (Get-Content -LiteralPath $wrappedFile -Raw).Trim()
    if ($wrapped) {
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $env:ComSpec
            $psi.Arguments = '/d /s /c "' + $wrapped + '"'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.StandardOutputEncoding = $utf8
            $p = [System.Diagnostics.Process]::Start($psi)
            $bytes = $utf8.GetBytes($raw)
            $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
            # Close the raw stream, not the writer: under a UTF-8 console the
            # writer appends a BOM on close, which corrupts the JSON downstream.
            $p.StandardInput.BaseStream.Close()
            $text = $p.StandardOutput.ReadToEnd()
            $p.WaitForExit()
            Write-Out $text
            exit 0
        } catch { }
    }
}

# No previous statusline: print a minimal one.
$esc = [char]27
$parts = @()
if ($data.model.display_name) { $parts += "$esc[36m[$($data.model.display_name)]$esc[0m" }
$pct = $data.rate_limits.five_hour.used_percentage
if ($null -ne $pct) {
    $p5 = [int][math]::Round([double]$pct)
    $color = if ($p5 -ge 90) { "$esc[31m" } elseif ($p5 -ge 70) { "$esc[33m" } else { "$esc[32m" }
    $parts += "5h: ${color}${p5}%$esc[0m"
}
Write-Out ($parts -join ' | ')
