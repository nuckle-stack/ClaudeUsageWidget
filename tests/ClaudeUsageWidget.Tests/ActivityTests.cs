using System.Text.Json;

namespace ClaudeUsageWidget.Tests;

/// <summary>What each session is doing now.</summary>
public class ActivityTests
{
    private readonly string _claudeDir = Path.Combine(Isolation.Root, "activity-" + Guid.NewGuid().ToString("N"));
    private const string Cwd = @"D:\Home Work\My Project";
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static Doing Describe(string tool, string input) =>
        ToolActivity.Describe(tool, JsonDocument.Parse(input).RootElement.Clone(), Now);

    // ---- a tool call in words -------------------------------------------------------

    [Theory]
    [InlineData("Edit", """{"file_path":"D:\\Home Work\\App\\Forecast.cs","old_string":"a","new_string":"b"}""", "editar Forecast.cs")]
    [InlineData("MultiEdit", """{"file_path":"/home/ana/app/main.py"}""", "editar main.py")]
    [InlineData("Write", """{"file_path":"D:\\p\\Diagnostics.cs","content":"..."}""", "escrever Diagnostics.cs")]
    [InlineData("Read", """{"file_path":"D:\\p\\Sessions.cs","offset":140}""", "ler Sessions.cs")]
    [InlineData("Grep", """{"pattern":"Titles\\.","path":"D:\\p"}""", "procurar \"Titles\\.\"")]
    [InlineData("Glob", """{"pattern":"**/*.cs"}""", "procurar ficheiros **/*.cs")]
    [InlineData("WebFetch", """{"url":"https://www.example.com/docs/page","prompt":"x"}""", "ler example.com")]
    [InlineData("WebSearch", """{"query":"claude code statusline"}""", "pesquisar \"claude code statusline\" na web")]
    [InlineData("Agent", """{"description":"Find the session readers","prompt":"..."}""", "delegar \"Find the session readers\"")]
    [InlineData("TodoWrite", """{"todos":[]}""", "atualizar a lista de tarefas")]
    [InlineData("Skill", """{"skill":"code-review"}""", "usar a skill code-review")]
    [InlineData("mcp__github__create_issue", """{}""", "usar create_issue (github)")]
    [InlineData("SomethingNew", """{}""", "usar SomethingNew")]
    [InlineData("Edit", """{}""", "editar um ficheiro")]
    public void Each_tool_reads_as_a_phrase(string tool, string input, string expected)
    {
        var d = Describe(tool, input);
        Assert.Equal(expected, d.Phrase);
        Assert.False(d.WaitsForYou);
    }

    [Theory]
    [InlineData("dotnet test tests/ClaudeUsageWidget.Tests", "Run the unit tests", "dotnet test tests/ClaudeUsageWidget.Tests")]
    // The "cd somewhere &&" in front says nothing about what runs.
    [InlineData("cd \"/d/Home Work/App\" && git push 2>&1 | tail -1", "", "git push 2>&1 | tail -1")]
    [InlineData("cd /d/p; cd sub && npm   run    build", "", "npm run build")]
    // A script says more through its description.
    [InlineData("$f = Get-Item x\nGet-Content $f", "Read the log", "\"Read the log\"")]
    [InlineData("for f in *.cs; do\n  echo $f\ndone", "List the sources", "\"List the sources\"")]
    [InlineData("$x = 1", "", "$x = 1")]
    [InlineData("", "", "um comando")]
    [InlineData("powershell -NoProfile -ExecutionPolicy Bypass -File \"D:\\Home Work\\App\\release.ps1\" -NotesFile notes.md", "", "powershell -NoProfile -ExecutionPolicy Bypass -…")]
    public void Commands_read_as_typed(string command, string description, string expected)
    {
        Assert.Equal(expected, ToolActivity.Command(command, description));
    }

    [Fact]
    public void Questions_and_plans_wait_for_you()
    {
        var question = Describe("AskUserQuestion", """{"questions":[]}""");
        Assert.True(question.WaitsForYou);
        Assert.Equal("Tem uma pergunta para ti.", question.Question);
        Assert.Equal("aprovar o plano", Describe("ExitPlanMode", "{}").Phrase);
    }

    // ---- reading it from the transcript ----------------------------------------------

    private string Transcript(string sessionId)
    {
        var dir = Path.Combine(_claudeDir, "projects", "D--Home-Work-My-Project");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, sessionId + ".jsonl");
    }

    private static string Block(object block, long at, bool sidechain = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["isSidechain"] = sidechain,
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object>
            {
                ["model"] = "claude-opus-5-5",
                ["content"] = new[] { block },
                ["usage"] = new Dictionary<string, object> { ["input_tokens"] = 2, ["cache_read_input_tokens"] = 1000, ["output_tokens"] = 10 },
            },
            ["timestamp"] = DateTimeOffset.FromUnixTimeSeconds(at).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        }) + "\n";

    private static string Call(string id, string tool, object input, long at, bool sidechain = false) =>
        Block(new Dictionary<string, object> { ["type"] = "tool_use", ["id"] = id, ["name"] = tool, ["input"] = input }, at, sidechain);

    private static string Thinking(long at) => Block(new Dictionary<string, object> { ["type"] = "thinking", ["thinking"] = "" }, at);

    private static string Result(string id) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = new[] { new Dictionary<string, object> { ["tool_use_id"] = id, ["type"] = "tool_result", ["content"] = "ok" } },
            },
        }) + "\n";

    private static readonly object EditForecast = new Dictionary<string, string> { ["file_path"] = @"D:\p\Forecast.cs" };
    private static readonly object RunTests = new Dictionary<string, string> { ["command"] = "dotnet test", ["description"] = "Run the tests" };

    private Doing? Activity(string id) => Transcripts.Activity(_claudeDir, id, Cwd);

    [Fact]
    public void A_call_without_a_result_is_what_the_session_is_doing()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Thinking(Now - 30) + Call("toolu_1", "Edit", EditForecast, Now - 20));
        var doing = Activity(id)!;
        Assert.Equal("editar Forecast.cs", doing.Phrase);
        Assert.Equal(Now - 20, doing.Since);
    }

    [Fact]
    public void Its_result_ends_it()
    {
        var id = Guid.NewGuid().ToString();
        var path = Transcript(id);
        File.WriteAllText(path, Call("toolu_1", "Bash", RunTests, Now - 20));
        Assert.Equal("correr dotnet test", Activity(id)!.Phrase);

        File.AppendAllText(path, Result("toolu_1"));
        Assert.Null(Activity(id));
    }

    [Fact]
    public void Calls_side_by_side_show_the_latest_until_each_is_done()
    {
        var id = Guid.NewGuid().ToString();
        var path = Transcript(id);
        File.WriteAllText(path, Call("toolu_1", "Edit", EditForecast, Now - 20) + Call("toolu_2", "Bash", RunTests, Now - 19));
        Assert.Equal("correr dotnet test", Activity(id)!.Phrase);
        File.AppendAllText(path, Result("toolu_2"));
        Assert.Equal("editar Forecast.cs", Activity(id)!.Phrase);
    }

    [Fact]
    public void Thinking_after_a_call_means_the_model_moved_on()
    {
        // e.g. a call whose result was cut off by an interruption
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Call("toolu_1", "Edit", EditForecast, Now - 20) + Thinking(Now - 5));
        Assert.Null(Activity(id));
    }

    [Fact]
    public void A_subagent_s_calls_are_not_the_session_s()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Call("toolu_1", "Read", EditForecast, Now - 20, sidechain: true));
        Assert.Null(Activity(id));
    }

    [Fact]
    public void A_result_found_inside_other_text_only_clears_its_own_call()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Call("toolu_1", "Edit", EditForecast, Now - 20) + Result("toolu_other"));
        Assert.Equal("editar Forecast.cs", Activity(id)!.Phrase);
    }

    // ---- the line in the sessions list -------------------------------------------------

    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(Now);

    private static SessionInfo Session(string status, Doing? doing = null, int statusMinutesAgo = 10) => new()
    {
        SessionId = "s",
        Name = "Projeto",
        Status = status,
        StatusUpdatedAt = At.AddMinutes(-statusMinutesAgo).ToUnixTimeMilliseconds(),
        Doing = doing,
    };

    private static Alert Input(string message) => new() { SessionId = "s", Name = "Projeto", Kind = AlertKind.Input, Message = message };

    [Fact]
    public void Busy_shows_the_call_and_how_long_it_has_run()
    {
        var doing = new Doing("correr dotnet test", null, Now - 120);
        Assert.Equal(("a correr dotnet test · há 2m", Tone.Busy), SessionStatus.Line(Session("busy", doing), null, At));
        Assert.Equal(("a trabalhar há 10m", Tone.Busy), SessionStatus.Line(Session("busy"), null, At));
    }

    [Fact]
    public void An_idle_session_ignores_a_call_left_behind()
    {
        var doing = new Doing("editar Forecast.cs", null, Now - 600);
        Assert.Equal(("parada há 10m", Tone.Idle), SessionStatus.Line(Session("idle", doing), null, At));
    }

    [Fact]
    public void A_permission_request_says_what_it_is_for()
    {
        var doing = new Doing("correr git push", null, Now - 60);
        var alert = Input("Claude needs your permission to use Bash");
        Assert.Equal(("precisa de ti para correr git push · há 10m", Tone.Input), SessionStatus.Line(Session("busy", doing), alert, At));
        Assert.Equal("Pede autorização para correr git push.", SessionStatus.AlertText(alert, Session("busy", doing)));
    }

    [Fact]
    public void Other_requests_keep_Claude_Code_s_words()
    {
        var doing = new Doing("correr git push", null, Now - 60);
        var alert = Input("Claude is waiting for your input");
        Assert.Equal("precisa de ti: Claude is waiting for your input · há 10m", SessionStatus.Line(Session("busy", doing), alert, At).Text);
        Assert.Equal("Claude is waiting for your input", SessionStatus.AlertText(alert, Session("busy", doing)));
        Assert.Equal("Claude needs your permission to use Bash", SessionStatus.AlertText(Input("Claude needs your permission to use Bash"), null));
    }

    [Fact]
    public void A_question_waits_for_you_even_before_the_alert()
    {
        var question = new Doing("responder a uma pergunta", "Tem uma pergunta para ti.", Now - 30);
        Assert.Equal(Tone.Input, SessionStatus.Line(Session("busy", question), null, At).Tone);
        Assert.StartsWith("precisa de ti para responder a uma pergunta", SessionStatus.Line(Session("busy", question), null, At).Text);
        Assert.Equal("Tem uma pergunta para ti.", SessionStatus.AlertText(Input("Claude is waiting for your input"), Session("busy", question)));
    }

    [Fact]
    public void A_finished_task_waits_for_you()
    {
        var done = new Alert { SessionId = "s", Name = "Projeto", Kind = AlertKind.Done, Message = "Tarefa concluída" };
        Assert.Equal(("terminou, à tua espera · há 10m", Tone.Done), SessionStatus.Line(Session("idle"), done, At));
    }
}
