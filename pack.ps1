# Builds the package to carry to another machine:
#   dist\ClaudeUsageWidget\  ClaudeUsageWidget.exe (installs itself: a double click),
#                            ClaudeUsageWidget.dll and its .json (the widget),
#                            runtime\ (the .NET it runs on),
#                            ClaudeUsageFeed.exe (Claude Code's statusline and hook),
#                            LEIA-ME.txt, LICENSE, install.ps1 (hands over to the exe)
#   dist\ClaudeUsageWidget.zip
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out = Join-Path $root 'dist\ClaudeUsageWidget'
$zip = Join-Path $root 'dist\ClaudeUsageWidget.zip'

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

# The widget is not one self-contained exe: that was 66 MB of new, unsigned,
# compressed code on every version, which unpacked part of .NET into %TEMP%
# on its first start, and a company antivirus held it for minutes before it
# showed. Now the exe is small and runs on the .NET in runtime\ (and only
# there: never on one installed on the machine). From one version to the next
# only the exe and the dll change; runtime\ is the same files, which the
# installer leaves alone.
dotnet publish (Join-Path $root 'ClaudeUsageWidget.csproj') -c Release -r win-x64 --self-contained false `
    -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=runtime `
    -p:DebugType=none -nologo -v q -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish do widget falhou.' }

# runtime\ is a .NET install of its own: the newest .NET 10 on this machine,
# the host and the two shared frameworks the widget uses. The translations of
# .NET's and WPF's messages stay out: none is Portuguese from Portugal, which
# already falls back to English.
$dotnet = Split-Path -Parent (Get-Command dotnet).Source
$fx = 'shared\Microsoft.NETCore.App', 'shared\Microsoft.WindowsDesktop.App'
$version = Get-ChildItem -LiteralPath (Join-Path $dotnet $fx[1]) -Directory -Name |
    Where-Object { $_ -match '^10\.\d+\.\d+$' -and
                   (Test-Path -LiteralPath (Join-Path $dotnet "host\fxr\$_")) -and
                   (Test-Path -LiteralPath (Join-Path $dotnet "$($fx[0])\$_")) } |
    Sort-Object { [version]$_ } | Select-Object -Last 1
if (-not $version) { throw "Não há um .NET 10 completo (host, NETCore.App e WindowsDesktop.App) em $dotnet." }
foreach ($rel in @('host\fxr') + $fx) {
    $target = Join-Path $out "runtime\$rel\$version"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $dotnet "$rel\$version") -Destination $target -Recurse
}
Get-ChildItem -LiteralPath (Join-Path $out 'runtime\shared') -Directory | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $_.FullName $version) -Directory | Remove-Item -Recurse -Force
}

# Claude Code starts the feed on every statusline refresh: trimmed and
# precompiled (ReadyToRun) it starts in about 80 ms; uncompressed, as
# decompressing would cost that again. On the .NET in runtime\ it took 90 ms,
# so it stays one exe; it builds to the same bytes until its code changes
# (no commit id in its version), and the installer then leaves it in place.
dotnet publish (Join-Path $root 'feed\ClaudeUsageFeed.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:DebugType=none -nologo -v q -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish do feed falhou.' }

Copy-Item -Path (Join-Path $root 'installer\*') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $out -Force

# Does it start, on its own .NET? Writes the diagnostics of a throwaway config folder.
$probe = Join-Path ([IO.Path]::GetTempPath()) "ClaudeUsageWidget-pack-$PID"
$savedConfig = $env:CLAUDE_CONFIG_DIR
$savedErrors = $env:DOTNET_DISABLE_GUI_ERRORS
try {
    $env:CLAUDE_CONFIG_DIR = $probe
    $env:DOTNET_DISABLE_GUI_ERRORS = '1'
    $p = Start-Process -FilePath (Join-Path $out 'ClaudeUsageWidget.exe') -ArgumentList '--diagnostics' -PassThru -Wait
    $report = Join-Path $probe 'usage-widget\diagnostico.txt'
    if ($p.ExitCode -ne 0 -or -not (Select-String -LiteralPath $report -Pattern "runtime\\shared" -Quiet)) {
        throw "O widget empacotado não arrancou com o .NET da pasta runtime (exit $($p.ExitCode))."
    }
}
finally {
    $env:CLAUDE_CONFIG_DIR = $savedConfig
    $env:DOTNET_DISABLE_GUI_ERRORS = $savedErrors
    Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip

$runtime = Get-ChildItem -LiteralPath (Join-Path $out 'runtime') -Recurse -File | Measure-Object Length -Sum
Get-ChildItem -LiteralPath $out -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
"runtime\ (.NET $version): $($runtime.Count) ficheiros, $([math]::Round($runtime.Sum / 1MB, 1)) MB"
"Pacote: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
