# Builds the package to carry to another machine:
#   dist\ClaudeUsageWidget\  (self-contained exe + install.ps1 + feed.ps1)
#   dist\ClaudeUsageWidget.zip
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out = Join-Path $root 'dist\ClaudeUsageWidget'
$zip = Join-Path $root 'dist\ClaudeUsageWidget.zip'

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

dotnet publish (Join-Path $root 'ClaudeUsageWidget.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -nologo -v q -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou.' }

Copy-Item -Path (Join-Path $root 'installer\*') -Destination $out -Force

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip

Get-ChildItem -LiteralPath $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
"Pacote: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
