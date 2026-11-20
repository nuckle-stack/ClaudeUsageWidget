<#
  Publishes a version on GitHub: builds the package (pack.ps1) and creates the
  release v<Version>, with the zip attached, so colleagues download it from
  the Releases page instead of getting it by hand.

    .\release.ps1                          # notes: the commits since the last release
    .\release.ps1 -NotesFile novidades.md  # notes written by hand, for colleagues

  First raise <Version> in ClaudeUsageWidget.csproj, commit and push: the
  release is built from the commit that is on GitHub, and each version is
  published once.
#>
[CmdletBinding()]
param([string]$NotesFile)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Native tools report through their exit code. Under 'Stop', Windows
# PowerShell would turn a line on stderr (progress, a warning) into a failure.
function Invoke-Tool([string]$exe) {
    $ErrorActionPreference = 'Continue'
    $out = & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $($args -join ' ') falhou (exit $LASTEXITCODE)." }
    $out
}

Push-Location $root
try {
    [xml]$proj = Get-Content -LiteralPath (Join-Path $root 'ClaudeUsageWidget.csproj') -Raw
    $version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $version) { throw 'Falta <Version> em ClaudeUsageWidget.csproj.' }
    $tag = "v$version"

    if (Invoke-Tool git status --porcelain) { throw 'Há alterações por gravar. Faz commit e push primeiro.' }
    Invoke-Tool git fetch --quiet --tags origin
    $head = Invoke-Tool git rev-parse HEAD
    if ($head -ne (Invoke-Tool git rev-parse '@{u}')) { throw 'Este commit não é o que está no GitHub. Faz push (ou pull) primeiro.' }
    if (Invoke-Tool git tag --list $tag) { throw "A versão $version já foi publicada ($tag). Sobe a <Version> no csproj." }

    & (Join-Path $root 'pack.ps1') | Out-Host
    $zip = Join-Path $root 'dist\ClaudeUsageWidget.zip'
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($NotesFile) {
        $news = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path).Trim()
    } else {
        $last = @(Invoke-Tool git tag --list 'v*' --sort=-v:refname) | Select-Object -First 1
        $range = if ($last) { "$last..HEAD" } else { 'HEAD' }
        $news = "## Alterações`n`n" + ((Invoke-Tool git log --no-merges '--format=- %s' $range) -join "`n")
    }

    # Single quotes: the backticks are Markdown, not PowerShell escapes.
    $template = @'
{{NEWS}}

## Instalar ou atualizar

1. Em **Assets**, descarrega `ClaudeUsageWidget.zip` e extrai-o.
2. Na pasta extraída, faz duplo clique em `ClaudeUsageWidget.exe` e carrega em **Instalar**. Se o Windows mostrar "O Windows protegeu o computador": **Mais informações** > **Executar mesmo assim** (o widget não é assinado).
3. No fim, a janela diz se os limites estão OK. Se não estiverem, tem um botão para copiar o diagnóstico: envia-o a quem te deu o widget.

Para atualizar à mão faz o mesmo: o instalador fecha o widget antigo e mantém as tuas definições. Com a versão 1.2.0 ou mais recente nem é preciso: o widget oferece a versão nova sozinho. O `LEIA-ME.txt` dentro do zip explica o resto.

SHA256 do zip: `{{SHA}}`
'@
    $body = $template.Replace('{{NEWS}}', $news).Replace('{{SHA}}', $sha)
    $notesPath = Join-Path ([IO.Path]::GetTempPath()) "claude-usage-widget-$tag.md"
    [IO.File]::WriteAllText($notesPath, $body, (New-Object System.Text.UTF8Encoding $false))
    try {
        Invoke-Tool gh release create $tag $zip --target $head --title "Claude Usage Widget $version" --notes-file $notesPath | Out-Host
    } finally {
        Remove-Item -LiteralPath $notesPath -ErrorAction SilentlyContinue
    }
    Invoke-Tool git fetch --quiet --tags origin
    "Publicado: $tag ($sha)"
} finally {
    Pop-Location
}
