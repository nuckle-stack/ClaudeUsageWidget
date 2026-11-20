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
real), os títulos das sessões, os lembretes dos avisos, o consumo por projeto,
o contexto ocupado de cada sessão (com e sem o tamanho da janela do
statusline), o que cada sessão está a fazer, as sessões recentes e o comando
que as retoma, a vigia do formato dos ficheiros do Claude Code (cada sinal, a
espera de 5 minutos, o aviso uma vez por versão), a parte do dia do limite
semanal, as atualizações e o diagnóstico (incluindo o que ele não pode levar:
nomes de sessões, valores de variáveis, a pasta do utilizador). Correm em menos
de um segundo.

## Diagnóstico

Quando algo não funciona noutro PC, quem lá está faz botão direito no widget >
**Copiar diagnóstico** e cola o texto numa mensagem. Sem o widget a correr:

```powershell
& "$env:LOCALAPPDATA\Programs\ClaudeUsageWidget\ClaudeUsageWidget.exe" --diagnostics
```

Isto grava o mesmo relatório em `~\.claude\usage-widget\diagnostico.txt`. O
`install.ps1` gera-o sozinho quando o teste do hello falha.

## Publicar uma versão

Cada versão vai para a página **Releases** do GitHub, com o zip anexado:

1. Sobe o `<Version>` em `ClaudeUsageWidget.csproj` (ex. `1.0.0` → `1.1.0`), faz
   commit e push.
2. Corre:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\release.ps1 -NotesFile novidades.md
   ```

O `release.ps1` recusa publicar se houver alterações por gravar, se o commit
ainda não estiver no GitHub ou se essa versão já existir. Gera o pacote com o
`pack.ps1` e cria a tag `v<Version>` e o release, com as instruções de
instalação e o SHA256 do zip. O `-NotesFile` é um Markdown com as novidades,
escritas para quem usa o widget; sem ele, as notas são a lista de commits desde
a versão anterior.

A versão aparece no diagnóstico (`1.0.0+<commit>`).

Os widgets instalados encontram o release novo sozinhos: perguntam ao GitHub
(`/releases/latest`, sem login) um minuto depois de arrancar e depois de 6 em 6
horas, avisam uma vez e, com o OK de quem o usa, descarregam o zip, conferem o
SHA256, correm o `install.ps1` dele com `-NoCheck` e fecham-se. A saída do
instalador fica em `~\.claude\usage-widget\update.log`. Só contam releases com
uma tag `vX.Y.Z` e o `ClaudeUsageWidget.zip` anexado; rascunhos e pre-releases
são ignorados.

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
as escritas em simultâneo, o ficheiro do contexto de cada sessão, a linha
impressa e a passagem para um statusline que já existisse. Demoram cerca de
15 segundos.

## Estrutura

| Pasta / ficheiro | O que é |
|---|---|
| `*.cs`, `*.xaml` | O widget (WPF, .NET 10) |
| `installer\` | `install.ps1`, `feed.ps1` (statusline), `notify.ps1` (hook de avisos), `LEIA-ME.txt` |
| `tests\ClaudeUsageWidget.Tests\` | Testes xUnit do widget |
| `tests\feed.Tests.ps1` | Testes Pester do `feed.ps1` |
| `pack.ps1` | Gera o pacote em `dist\` |
| `release.ps1` | Publica a versão do `.csproj` como release no GitHub, com o zip |
