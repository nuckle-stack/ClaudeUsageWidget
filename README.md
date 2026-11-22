# Claude Usage Widget

Widget para Windows que mostra os limites de utilização do Claude Code (janela de
5 horas e semanal), lista as sessões abertas (CLI, app Desktop, VS Code) e avisa
quando uma sessão termina, precisa de ti ou está a gastar depressa.

Para instalar e usar, vê [`installer/LEIA-ME.txt`](installer/LEIA-ME.txt), que vai
dentro do zip. Em resumo: extrair o zip e fazer duplo clique em
`ClaudeUsageWidget.exe`. Não é preciso PowerShell nem administrador.

## Requisitos para desenvolver

- Windows de 64 bits
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Compilar e gerar o pacote

```powershell
dotnet build                                            # compila para bin\
powershell -ExecutionPolicy Bypass -File .\pack.ps1     # gera dist\ClaudeUsageWidget.zip
```

O `pack.ps1` publica dois executáveis autónomos (não precisam de .NET instalado):

- `ClaudeUsageWidget.exe`, o widget. Corrido a partir do zip, instala-se a si
  próprio (ver **Instalar** abaixo).
- `ClaudeUsageFeed.exe`, o statusline e o hook de avisos que o Claude Code
  chama (projeto em `feed\`). Corre a cada atualização do statusline, por isso
  é pequeno e pré-compilado: arranca em cerca de 70 ms.

Junta-lhes o `LEIA-ME.txt`, a `LICENSE` e o `install.ps1`.

## Instalar

O widget instala-se sozinho, a partir do próprio exe:

| Comando | O que faz |
|---|---|
| duplo clique no `ClaudeUsageWidget.exe` do zip | Janela de instalação (ou de atualização, se já houver uma versão instalada) |
| `ClaudeUsageWidget.exe --install` | O mesmo |
| `… --install --silent [--no-check] [--autostart\|--no-autostart] [--no-launch] [--dir <pasta>]` | Sem janela; os passos ficam em `~\.claude\usage-widget\install.log`. É o que as atualizações usam |
| `… --uninstall [--silent]` | Repõe o `settings.json` e apaga o widget e os dados dele |

A instalação:

- copia os dois exes para `%LOCALAPPDATA%\Programs\ClaudeUsageWidget`;
- aponta o `statusLine` e o hook `Notification` do `settings.json` do Claude
  Code para o `ClaudeUsageFeed.exe`, depois de uma cópia de segurança. Um
  statusline que já existisse fica em `wrapped-command.txt` e continua a ser
  chamado. As instalações antigas, com `feed.ps1` e `notify.ps1` em PowerShell,
  são convertidas;
- liga o arranque com o Windows, se pedido;
- faz o teste do hello.

O `install.ps1` só passa a vez ao exe. Fica para as instruções antigas e para os
widgets 1.2 a 1.7, cujas atualizações o correm com `-NoCheck`.

## Testes

```powershell
dotnet test tests\ClaudeUsageWidget.Tests
```

Não tocam nos ficheiros reais em `~\.claude\usage-widget`: usam uma pasta
temporária como `CLAUDE_CONFIG_DIR`. Correm em menos de um segundo.

Cobrem:

- a previsão do ritmo de uso e os avisos de 80/95%, o agendamento do auto-hello
  e a leitura da saída do `claude -p` (com uma linha `rate_limit_event` real);
- o que o `/usage` mostrou da última vez, que o Claude Code guarda no
  `.claude.json`: arredonda de outra maneira que o statusline e pode ir 1%
  acima, por isso o widget fica com o mais alto da mesma janela;
- os títulos das sessões, os lembretes dos avisos, o consumo por projeto, o
  contexto ocupado de cada sessão (com e sem o tamanho da janela do
  statusline), o que cada sessão está a fazer e as sessões recentes e o comando
  que as retoma;
- a vigia do formato dos ficheiros do Claude Code (cada sinal, a espera de 5
  minutos, o aviso uma vez por versão), a parte do dia do limite semanal e o
  resumo de cada semana;
- as atualizações e o diagnóstico, incluindo o que ele não pode levar: nomes de
  sessões, valores de variáveis, a pasta do utilizador;
- o `ClaudeUsageFeed.exe`: a junção das leituras de várias sessões, as escritas
  em simultâneo, o ficheiro do contexto de cada sessão, a linha impressa, a
  passagem para um statusline que já existisse e o próprio programa a correr
  com o JSON no stdin;
- o instalador: cada ponto de partida do `settings.json` (vazio, com o
  PowerShell antigo, com o statusline de outra pessoa, já instalado, noutra
  pasta), a desinstalação, comentários e acentos no `settings.json`, e a troca
  de um exe em uso.

## Diagnóstico

Quando algo não funciona noutro PC, quem lá está faz botão direito no widget >
**Copiar diagnóstico** e cola o texto numa mensagem. Sem o widget a correr:

```powershell
& "$env:LOCALAPPDATA\Programs\ClaudeUsageWidget\ClaudeUsageWidget.exe" --diagnostics
```

Isto grava o mesmo relatório em `~\.claude\usage-widget\diagnostico.txt`. A
instalação gera-o sozinha quando o teste do hello falha, e a janela de
instalação tem um botão para o copiar.

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

Os widgets instalados encontram o release novo sozinhos. Perguntam ao GitHub
(`/releases/latest`, sem login) um minuto depois de arrancar e depois de 6 em 6
horas, e avisam uma vez. Com o OK de quem o usa:

1. descarregam o zip e conferem o SHA256;
2. correm o `ClaudeUsageWidget.exe --install --silent --no-check` da versão
   nova e fecham-se.

Os passos ficam em `~\.claude\usage-widget\install.log`. Só contam releases com
uma tag `vX.Y.Z` e o `ClaudeUsageWidget.zip` anexado; rascunhos e pre-releases
são ignorados.

## Estrutura

| Pasta / ficheiro | O que é |
|---|---|
| `*.cs`, `*.xaml` | O widget (WPF, .NET 10), com o instalador (`Installer.cs`, `InstallWindow`) |
| `feed\` | `ClaudeUsageFeed.exe`: o statusline e o hook de avisos do Claude Code |
| `installer\` | `LEIA-ME.txt` e o `install.ps1` que passa a vez ao exe |
| `tests\ClaudeUsageWidget.Tests\` | Testes xUnit do widget, do feed e do instalador |
| `pack.ps1` | Gera o pacote em `dist\` |
| `release.ps1` | Publica a versão do `.csproj` como release no GitHub, com o zip |

## Licença

MIT: ver [LICENSE](LICENSE).
