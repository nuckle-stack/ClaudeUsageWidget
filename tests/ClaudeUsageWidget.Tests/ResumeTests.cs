namespace ClaudeUsageWidget.Tests;

/// <summary>Opening a past session in a terminal.</summary>
public class ResumeTests
{
    private const string Claude = @"C:\Users\Ana Silva\.local\bin\claude.exe";
    private const string Id = "a76f3eaa-7b50-4fda-b45d-5b66f03baee9";

    [Fact]
    public void A_new_Windows_Terminal_tab_in_the_session_s_folder()
    {
        Assert.Equal(["-w", "0", "new-tab", "-d", @"D:\Home Work\App", Claude, "--resume", Id],
            Resume.TerminalArguments(@"D:\Home Work\App\", Claude, Id));
    }

    [Fact]
    public void A_console_keeps_the_quoted_path_whole()
    {
        // cmd drops the outer pair of quotes and keeps the ones around the path.
        Assert.Equal($"/k \"\"{Claude}\" --resume {Id}\"", Resume.ConsoleArguments(Claude, Id));
    }

    [Theory]
    [InlineData(@"D:\Home Work\App", @"D:\Home Work\App")]
    [InlineData(@"D:\Home Work\App\", @"D:\Home Work\App")]
    [InlineData(@"D:\", @"D:\.")]
    [InlineData("D:", @"D:\.")]
    public void Folders_never_end_in_a_backslash_that_would_escape_a_quote(string dir, string expected)
    {
        Assert.Equal(expected, Resume.SafeDir(dir));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a76f3eaa; calc")]
    [InlineData("\"--dangerously\"")]
    public void Only_a_plain_session_id_is_passed_on(string id)
    {
        var (ok, detail) = Resume.Open(id, @"D:\", "claude");
        Assert.False(ok);
        Assert.Equal("Sessão sem identificador válido.", detail);
    }
}
