namespace ClaudeUsageWidget.Tests;

public class SessionTitleTests
{
    private readonly string _claudeDir = Path.Combine(Isolation.Root, "titles-" + Guid.NewGuid().ToString("N"));
    private const string Cwd = @"D:\Home Work\My Project";

    /// <summary>Where Claude Code keeps the transcript of a session started in Cwd.</summary>
    private string Transcript(string sessionId, string folder = "D--Home-Work-My-Project")
    {
        var dir = Path.Combine(_claudeDir, "projects", folder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, sessionId + ".jsonl");
    }

    private static string Ai(string title) => $$"""{"type":"ai-title","aiTitle":"{{title}}","sessionId":"x"}""" + "\n";
    private static string Custom(string title) => $$"""{"type":"custom-title","customTitle":"{{title}}","sessionId":"x"}""" + "\n";
    private const string Chatter = """{"type":"user","message":{"content":"hi"}}""" + "\n";

    private string For(string id, string name = "project-12", string source = "derived") =>
        Transcripts.Title(_claudeDir, id, Cwd, name, source);

    [Fact]
    public void Generated_title_replaces_the_folder_name()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Chatter + Ai("Estado do projeto") + Chatter);
        Assert.Equal("Estado do projeto", For(id));
    }

    [Fact]
    public void Without_a_transcript_the_name_stays()
    {
        Assert.Equal("project-12", For(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void A_renamed_session_wins_over_the_generated_title()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Custom("Como resolvo?") + Ai("Generated later"));
        Assert.Equal("Como resolvo?", For(id));
    }

    [Fact]
    public void A_name_not_derived_from_the_folder_wins_over_the_generated_title()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Ai("Generated"));
        Assert.Equal("Teste de nova sessão", For(id, "Teste de nova sessão", "user"));
    }

    [Fact]
    public void New_lines_update_the_title_and_half_written_ones_wait()
    {
        var id = Guid.NewGuid().ToString();
        var path = Transcript(id);
        File.WriteAllText(path, Ai("First"));
        Assert.Equal("First", For(id));

        var next = Ai("Second");
        File.AppendAllText(path, next[..20]);          // still being written
        Assert.Equal("First", For(id));
        File.AppendAllText(path, next[20..]);
        Assert.Equal("Second", For(id));
    }

    [Fact]
    public void A_transcript_in_another_project_folder_is_found()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id, "somewhere-else"), Ai("Moved"));
        Assert.Equal("Moved", For(id));
    }

    [Fact]
    public void Only_the_tail_of_a_large_transcript_is_read_and_it_has_the_title()
    {
        var id = Guid.NewGuid().ToString();
        var path = Transcript(id);
        using (var w = new StreamWriter(path))
        {
            w.Write(Ai("Old title"));
            var filler = Chatter.PadRight(1000, ' ').Replace("\n", "") + "\n";
            for (var i = 0; i < 6000; i++) w.Write(filler);   // ~6 MB, past the first-read window
            w.Write(Ai("Current title"));
        }
        Assert.Equal("Current title", For(id));
    }

    [Theory]
    [InlineData("✳ Estado do projeto", "Estado do projeto", true)]
    [InlineData("◑ estado do PROJETO ", "Estado do projeto", true)]
    [InlineData("✳ Estado do projeto claudewidget", "Estado do projeto", false)]
    [InlineData("✳ Estado do projeto", "Estado do projeto claudewidget", false)]
    [InlineData("Claude", "Estado do projeto", false)]
    [InlineData("anything", "", false)]
    public void SameTitle(string caption, string title, bool expected)
    {
        Assert.Equal(expected, WindowFocus.SameTitle(caption, title));
    }
}
