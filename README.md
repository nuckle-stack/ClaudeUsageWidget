# Claude Usage Widget

Widget para Windows que mostra os limites de utilização do Claude Code (janela de
5 horas e semanal), lista as sessões abertas (CLI, app Desktop, VS Code) e avisa
quando uma sessão termina, precisa de ti ou está a gastar depressa.

Para instalar e usar, vê [`installer/LEIA-ME.txt`](installer/LEIA-ME.txt), que vai
dentro do zip.

## Requisitos para desenvolver

- Windows de 64 bits
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows PowerShell 5.1 (já vem com o Windows)
- [Pester](https://pester.dev) 5 ou mais recente, só para os testes do `feed.ps1`

## Compilar e gerar o pacote

```powershell
dotnet build                                            # compila para bin\
powershell -ExecutionPolicy Bypass -File .\pack.ps1     # gera dist\ClaudeUsageWidget.zip
```

O `pack.ps1` publica um exe único e autónomo (não precisa de .NET instalado) e
junta-lhe os scripts de `installer\`.

## Testes

Há duas baterias de testes. Nenhuma toca nos ficheiros reais em
`~\.claude\usage-widget`: as duas usam uma pasta temporária como
`CLAUDE_CONFIG_DIR`.

### Widget (C#, xUnit)

```powershell
dotnet test tests\ClaudeUsageWidget.Tests
```

Cobrem a previsão do ritmo de uso e os avisos de 80/95%, o agendamento do
auto-hello, a leitura da saída do `claude -p` (com uma linha `rate_limit_event`
real), os títulos das sessões e os lembretes dos avisos. Correm em menos de um
segundo.

### Statusline (`feed.ps1`, Pester)

Na primeira vez, instala o Pester. O Windows traz a versão 3.4, que é antiga
demais; `-SkipPublisherCheck` é necessário por causa dessa versão:

```powershell
Install-Module Pester -MinimumVersion 5.0 -Scope CurrentUser -Force -SkipPublisherCheck
```

Depois:

```powershell
Set-ExecutionPolicy -Scope Process Bypass   # só para esta janela; sem isto o Pester não carrega
Invoke-Pester .\tests\feed.Tests.ps1 -Output Detailed
```

O Windows bloqueia scripts por omissão (política `Restricted`), incluindo os do
próprio Pester; a primeira linha levanta isso apenas na janela atual.

Cada teste corre o `feed.ps1` verdadeiro num processo à parte, com o JSON no
stdin como faz o Claude Code. Cobrem a junção das leituras de várias sessões,
as escritas em simultâneo, a linha impressa e a passagem para um statusline que
já existisse. Demoram cerca de 15 segundos.

## Estrutura

| Pasta / ficheiro | O que é |
|---|---|
| `*.cs`, `*.xaml` | O widget (WPF, .NET 10) |
| `installer\` | `install.ps1`, `feed.ps1` (statusline), `notify.ps1` (hook de avisos), `LEIA-ME.txt` |
| `tests\ClaudeUsageWidget.Tests\` | Testes xUnit do widget |
| `tests\feed.Tests.ps1` | Testes Pester do `feed.ps1` |
| `pack.ps1` | Gera o pacote em `dist\` |
