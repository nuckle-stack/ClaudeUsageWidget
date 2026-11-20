# Builds the package to carry to another machine:
#   dist\ClaudeUsageWidget\  ClaudeUsageWidget.exe (installs itself: a double click),
#                            ClaudeUsageFeed.exe (Claude Code's statusline and hook),
#                            LEIA-ME.txt, LICENSE, install.ps1 (hands over to the exe)
#   dist\ClaudeUsageWidget.zip
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out = Join-Path $root 'dist\ClaudeUsageWidget'
$zip = Join-Path $root 'dist\ClaudeUsageWidget.zip'

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

dotnet publish (Join-Path $root 'ClaudeUsageWidget.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -nologo -v q -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish do widget falhou.' }

# Claude Code starts the feed on every statusline refresh: trimmed and
# precompiled (ReadyToRun) it starts in about 70 ms; uncompressed, as
# decompressing would cost that again.
dotnet publish (Join-Path $root 'feed\ClaudeUsageFeed.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:DebugType=none -nologo -v q -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish do feed falhou.' }

Copy-Item -Path (Join-Path $root 'installer\*') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $out -Force

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip

Get-ChildItem -LiteralPath $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
"Pacote: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
