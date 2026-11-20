# Pester 5+ tests for installer\feed.ps1, the statusline command.
#   Invoke-Pester .\tests\feed.Tests.ps1 -Output Detailed
# Each test runs the real script in its own powershell.exe, as Claude Code
# does: the statusline JSON on stdin, a throwaway CLAUDE_CONFIG_DIR, and the
# printed line read back from stdout.

BeforeAll {
    $script:Feed = (Resolve-Path (Join-Path $PSScriptRoot '..\installer\feed.ps1')).Path
    $script:Utf8 = New-Object System.Text.UTF8Encoding $false
    $script:Esc = [char]27

    function New-ConfigDir {
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $dir 'usage-widget') -Force | Out-Null
        $dir
    }

    # Runs feed.ps1 with this stdin (a string, sent as UTF-8 without BOM).
    # Returns @{ Out = stdout text; Exit = exit code }.
    function Invoke-Feed([string]$ConfigDir, [string]$Stdin) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell.exe'
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script:Feed`""
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.StandardOutputEncoding = $script:Utf8
        $psi.EnvironmentVariables['CLAUDE_CONFIG_DIR'] = $ConfigDir
        $p = [System.Diagnostics.Process]::Start($psi)
        $bytes = $script:Utf8.GetBytes($Stdin)
        $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $p.StandardInput.BaseStream.Close()
        $out = $p.StandardOutput.ReadToEnd()
        if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'feed.ps1 did not finish in 60 s' }
        @{ Out = $out; Exit = $p.ExitCode }
    }

    # The statusline JSON Claude Code sends; pass $null to leave a window out.
    function New-Input($Five, $Week, [string]$Model = 'Opus 5.5') {
        $limits = [ordered]@{}
        if ($Five) { $limits.five_hour = [ordered]@{ used_percentage = $Five[0]; resets_at = $Five[1] } }
        if ($Week) { $limits.seven_day = [ordered]@{ used_percentage = $Week[0]; resets_at = $Week[1] } }
        [ordered]@{ model = @{ display_name = $Model }; rate_limits = $limits } | ConvertTo-Json -Depth 5 -Compress
    }

    function Get-Limits([string]$ConfigDir) {
        Get-Content -LiteralPath (Join-Path $ConfigDir 'usage-widget\limits.json') -Raw | ConvertFrom-Json
    }

    function Set-Limits([string]$ConfigDir, $Five, $Week) {
        $old = [ordered]@{ updated_at = 1 }
        if ($Five) { $old.five_hour = [ordered]@{ used_percentage = $Five[0]; resets_at = $Five[1] } }
        if ($Week) { $old.seven_day = [ordered]@{ used_percentage = $Week[0]; resets_at = $Week[1] } }
        [IO.File]::WriteAllText((Join-Path $ConfigDir 'usage-widget\limits.json'), ($old | ConvertTo-Json -Depth 4))
    }

    $script:R5 = 1795137600   # a five-hour reset
    $script:R7 = 1795543200   # a weekly reset
}

Describe 'feed.ps1 writes limits.json' {
    It 'records both windows and the time of the reading' {
        $dir = New-ConfigDir
        $before = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        (Invoke-Feed $dir (New-Input @(12.5, $R5) @(3, $R7))).Exit | Should -Be 0

        $l = Get-Limits $dir
        $l.five_hour.used_percentage | Should -Be 12.5
        $l.five_hour.resets_at | Should -Be $R5
        $l.seven_day.used_percentage | Should -Be 3
        $l.seven_day.resets_at | Should -Be $R7
        $l.updated_at | Should -BeGreaterOrEqual $before
    }

    It 'creates the usage-widget folder on a fresh machine' {
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        Invoke-Feed $dir (New-Input @(1, $R5) @(1, $R7)) | Out-Null
        Join-Path $dir 'usage-widget\limits.json' | Should -Exist
    }

    It 'does not touch limits.json when the input has no rate limits' {
        $dir = New-ConfigDir
        (Invoke-Feed $dir '{"model":{"display_name":"Opus 5.5"}}').Exit | Should -Be 0
        Join-Path $dir 'usage-widget\limits.json' | Should -Not -Exist
    }

    It 'leaves no temp file behind' {
        $dir = New-ConfigDir
        Invoke-Feed $dir (New-Input @(1, $R5) @(1, $R7)) | Out-Null
        Get-ChildItem (Join-Path $dir 'usage-widget') -Filter '*.tmp' | Should -BeNullOrEmpty
    }

    It 'leaves no temp file and a valid limits.json when sessions write at once' {
        $dir = New-ConfigDir
        $json = New-Input @(5, $R5) @(2, $R7)
        $jobs = 1..8 | ForEach-Object {
            Start-Job -ScriptBlock {
                param($feed, $dir, $json)
                $env:CLAUDE_CONFIG_DIR = $dir
                $json | powershell -NoProfile -ExecutionPolicy Bypass -File $feed | Out-Null
            } -ArgumentList $script:Feed, $dir, $json
        }
        $jobs | Wait-Job -Timeout 120 | Remove-Job -Force

        Get-ChildItem (Join-Path $dir 'usage-widget') -Filter '*.tmp' | Should -BeNullOrEmpty
        (Get-Limits $dir).five_hour.used_percentage | Should -Be 5
    }
}

Describe 'feed.ps1 merges readings from several sessions' {
    # Several sessions write the same file, and an idle one can carry stale
    # numbers: per window, a later reset wins, and within the same window the
    # higher usage wins.

    It 'same window: a higher usage replaces the old one' {
        $dir = New-ConfigDir
        Set-Limits $dir @(10, $R5) @(3, $R7)
        Invoke-Feed $dir (New-Input @(20, $R5) @(4, $R7)) | Out-Null
        (Get-Limits $dir).five_hour.used_percentage | Should -Be 20
        (Get-Limits $dir).seven_day.used_percentage | Should -Be 4
    }

    It 'same window: a lower usage from a stale session is ignored' {
        $dir = New-ConfigDir
        Set-Limits $dir @(30, $R5) @(8, $R7)
        Invoke-Feed $dir (New-Input @(10, $R5) @(5, $R7)) | Out-Null
        (Get-Limits $dir).five_hour.used_percentage | Should -Be 30
        (Get-Limits $dir).seven_day.used_percentage | Should -Be 8
    }

    It 'resets up to a minute apart count as the same window' {
        $dir = New-ConfigDir
        Set-Limits $dir @(30, $R5) $null
        Invoke-Feed $dir (New-Input @(10, ($R5 + 60)) $null) | Out-Null
        $l = Get-Limits $dir
        $l.five_hour.used_percentage | Should -Be 30
        $l.five_hour.resets_at | Should -Be $R5
    }

    It 'a new window wins even with lower usage' {
        $dir = New-ConfigDir
        Set-Limits $dir @(95, $R5) $null
        Invoke-Feed $dir (New-Input @(2, ($R5 + 5 * 3600)) $null) | Out-Null
        $l = Get-Limits $dir
        $l.five_hour.used_percentage | Should -Be 2
        $l.five_hour.resets_at | Should -Be ($R5 + 5 * 3600)
    }

    It 'a reading from the previous window is ignored' {
        $dir = New-ConfigDir
        Set-Limits $dir @(2, $R5) $null
        Invoke-Feed $dir (New-Input @(95, ($R5 - 5 * 3600)) $null) | Out-Null
        (Get-Limits $dir).five_hour.resets_at | Should -Be $R5
        (Get-Limits $dir).five_hour.used_percentage | Should -Be 2
    }

    It 'keeps a window the new reading does not carry' {
        $dir = New-ConfigDir
        Set-Limits $dir @(10, $R5) @(6, $R7)
        Invoke-Feed $dir (New-Input @(12, $R5) $null) | Out-Null
        $l = Get-Limits $dir
        $l.five_hour.used_percentage | Should -Be 12
        $l.seven_day.used_percentage | Should -Be 6
        $l.seven_day.resets_at | Should -Be $R7
    }

    It 'replaces a corrupt limits.json' {
        $dir = New-ConfigDir
        [IO.File]::WriteAllText((Join-Path $dir 'usage-widget\limits.json'), '{ not json')
        Invoke-Feed $dir (New-Input @(7, $R5) @(1, $R7)) | Out-Null
        (Get-Limits $dir).five_hour.used_percentage | Should -Be 7
    }
}

Describe 'feed.ps1 prints a statusline' {
    It 'shows the model and the 5h usage in <Color> at <Pct>%' -ForEach @(
        @{ Pct = 12; Color = 'green'; Code = '32' }
        @{ Pct = 69.6; Color = 'yellow'; Code = '33' }   # rounds to 70
        @{ Pct = 89; Color = 'yellow'; Code = '33' }
        @{ Pct = 90; Color = 'red'; Code = '31' }
    ) {
        $out = (Invoke-Feed (New-ConfigDir) (New-Input @($Pct, $R5) @(1, $R7))).Out
        $shown = [int][math]::Round([double]$Pct)
        $out | Should -Be "$Esc[36m[Opus 5.5]$Esc[0m | 5h: $Esc[$($Code)m$shown%$Esc[0m"
    }

    It 'shows only the model when there are no limits' {
        $out = (Invoke-Feed (New-ConfigDir) '{"model":{"display_name":"Haiku"}}').Out
        $out | Should -Be "$Esc[36m[Haiku]$Esc[0m"
    }

    It 'keeps accents intact (UTF-8 out)' {
        $out = (Invoke-Feed (New-ConfigDir) '{"model":{"display_name":"Ação ✳"}}').Out
        $out | Should -Be "$Esc[36m[Ação ✳]$Esc[0m"
    }

    It 'never fails the session on bad input' -ForEach @(
        @{ Name = 'empty'; Stdin = '' }
        @{ Name = 'not JSON'; Stdin = 'hello {' }
        @{ Name = 'odd types'; Stdin = '{"rate_limits":{"five_hour":"x","seven_day":[1,2]}}' }
    ) {
        (Invoke-Feed (New-ConfigDir) $Stdin).Exit | Should -Be 0
    }
}

Describe 'feed.ps1 hands over to the previous statusline' {
    BeforeAll {
        # Stands in for the user's own statusline: reports the first byte it
        # received (a BOM would be 239) and the model it parsed from the JSON.
        $script:Echo = Join-Path $TestDrive 'echo-statusline.ps1'
        Set-Content -LiteralPath $script:Echo -Encoding ASCII -Value @'
$in = [Console]::OpenStandardInput()
$ms = New-Object IO.MemoryStream
$in.CopyTo($ms)
$bytes = $ms.ToArray()
$json = (New-Object Text.UTF8Encoding $false).GetString($bytes) | ConvertFrom-Json
[Console]::Out.Write("first=$($bytes[0]) model=$($json.model.display_name)")
'@
    }

    It 'passes the same JSON on, without a BOM, and prints its output' {
        $dir = New-ConfigDir
        $cmd = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$script:Echo`""
        [IO.File]::WriteAllText((Join-Path $dir 'usage-widget\wrapped-command.txt'), $cmd)

        $r = Invoke-Feed $dir (New-Input @(40, $R5) @(9, $R7) -Model 'Sonnet')
        $r.Out | Should -Be 'first=123 model=Sonnet'
        (Get-Limits $dir).five_hour.used_percentage | Should -Be 40   # still recorded
    }

    It 'falls back to its own line when the file is empty' {
        $dir = New-ConfigDir
        [IO.File]::WriteAllText((Join-Path $dir 'usage-widget\wrapped-command.txt'), "  `r`n")
        (Invoke-Feed $dir (New-Input @(12, $R5) $null)).Out | Should -BeLike "*5h: *12%*"
    }
}
