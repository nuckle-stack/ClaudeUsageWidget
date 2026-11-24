using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

public enum NoteKind { Heading, Bullet, Text }

/// <summary>A piece of a line of the notes: bold or not.</summary>
public record NoteRun(string Text, bool Bold);

/// <summary>A heading, a bullet or a paragraph of the notes, ready to draw.</summary>
public record NoteBlock(NoteKind Kind, IReadOnlyList<NoteRun> Runs)
{
    public string Plain => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// The release notes, as written for GitHub (a little Markdown: headings,
/// bullets, bold), read into blocks the widget draws in its own look.
/// </summary>
internal static class ReleaseNotes
{
    public static List<NoteBlock> Parse(string markdown)
    {
        var blocks = new List<NoteBlock>();
        NoteKind? kind = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (kind is { } k && text.ToString().Trim() is { Length: > 0 } done) blocks.Add(new(k, Runs(done)));
            kind = null;
            text.Clear();
        }

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (Regex.Match(line, @"^#+\s*(.*)$") is { Success: true } heading)
            {
                Flush();
                kind = NoteKind.Heading;
                text.Append(heading.Groups[1].Value);
                Flush();
                continue;
            }
            if (Regex.Match(line, @"^(?:[-*]|\d+\.)\s+(.*)$") is { Success: true } bullet)
            {
                Flush();
                kind = NoteKind.Bullet;
                text.Append(bullet.Groups[1].Value);
                continue;
            }
            // A line that goes on from the one before, or starts a paragraph.
            if (kind is null) kind = NoteKind.Text;
            else text.Append(' ');
            text.Append(line);
        }
        Flush();
        return blocks;
    }

    /// <summary>
    /// The notes of several versions, newest first, each under its number.
    /// One version keeps its notes as they are.
    /// </summary>
    public static string Combine(IReadOnlyList<(Version Version, string Notes)> releases)
    {
        if (releases.Count == 1) return releases[0].Notes;
        var all = new StringBuilder();
        foreach (var (version, notes) in releases.OrderByDescending(r => r.Version))
        {
            // Their own "Novidades" heading gives way to the version's number.
            var body = Regex.Replace(notes.Replace("\r", ""), @"^#+\s*Novidades\s*\n", "", RegexOptions.IgnoreCase).Trim();
            all.Append($"## Versão {version.ToString(3)}\n\n{body}\n\n");
        }
        return all.ToString().Trim();
    }

    /// <summary>Bold where the text has **, without the Markdown for code and links.</summary>
    private static List<NoteRun> Runs(string line)
    {
        line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1").Replace("`", "");
        var parts = line.Split("**");
        var runs = new List<NoteRun>();
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) runs.Add(new(parts[i], i % 2 == 1));
        return runs;
    }
}
