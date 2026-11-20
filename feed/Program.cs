using System.Text;
using ClaudeUsageFeed;

// ClaudeUsageFeed statusline | notify   (Claude Code's JSON on stdin)
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "statusline";
if (!Console.IsInputRedirected)
{
    // Started by hand: there is no JSON to wait for.
    Console.Error.WriteLine("ClaudeUsageFeed: o statusline e o hook do Claude Usage Widget. O Claude Code chama-o; não é para abrir à mão.");
    return 0;
}

try
{
    string input;
    using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
        input = reader.ReadToEnd();

    if (mode == "notify") Feed.Notify(input, Feed.ClaudeDir);
    else
    {
        var bytes = new UTF8Encoding(false).GetBytes(Feed.Statusline(input, Feed.ClaudeDir));
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
    }
}
catch { }

// Never fail or block the Claude Code session because of the widget.
return 0;
