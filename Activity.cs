using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>
/// What a session is doing right now: the tool call it made and has no
/// result for yet. Phrase is an infinitive ("editar Forecast.cs"), shown as
/// "a editar Forecast.cs" while it runs and "precisa de ti para editar
/// Forecast.cs" when it waits for permission. Question is set for the calls
/// that are themselves a question to you (a question, a plan to approve).
/// Tool is the tool's name as Claude Code calls it ("Bash", "Agent"...).
/// </summary>
public record Doing(string Phrase, string? Question, long Since, string Tool = "")
{
    public bool WaitsForYou => Question is not null;

    /// <summary>Work handed to a subagent, whose progress is not written in the session's own conversation.</summary>
    public bool Delegates => Tool is "Task" or "Agent";
}

/// <summary>How a session's line in the list reads.</summary>
public enum Tone { Busy, Idle, Input, Done }

public static class SessionStatus
{
    /// <summary>
    /// "a editar Forecast.cs · há 2m", "precisa de ti para correr dotnet test",
    /// "parada há 13h45"... An alert wins over the session's own state, and the
    /// call it waits on says what a permission request is for.
    /// </summary>
    public static (string Text, Tone Tone) Line(SessionInfo s, Alert? alert, DateTimeOffset now)
    {
        // A call left over from an interrupted turn means nothing once the session stopped.
        var doing = s.Status == "idle" ? null : s.Doing;
        var asks = doing is not null && (doing.WaitsForYou || alert?.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) == true);

        string text;
        Tone tone;
        var stamp = s.StatusUpdatedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s.StatusUpdatedAt) : (DateTimeOffset?)null;
        var dot = true;
        if (alert is { Kind: AlertKind.Input })
            (text, tone) = (asks ? $"precisa de ti para {doing!.Phrase}" : $"precisa de ti: {alert.Message}", Tone.Input);
        else if (alert is { Kind: AlertKind.Done })
            (text, tone) = ("terminou, à tua espera", Tone.Done);
        else if (doing is { WaitsForYou: true })
            (text, tone) = ($"precisa de ti para {doing.Phrase}", Tone.Input);
        else if (s.Working && doing is not null)
        {
            (text, tone) = ($"a {doing.Phrase}", Tone.Busy);
            // How long this call has been running, not the whole turn.
            if (doing.Since > 0) stamp = DateTimeOffset.FromUnixTimeSeconds(doing.Since);
        }
        else
        {
            (text, tone) = s.Status switch
            {
                "busy" => ("a trabalhar", Tone.Busy),
                "shell" => ("a correr um comando", Tone.Busy),
                "waiting" => (s.WaitingFor.Length > 0 ? $"à tua espera: {s.WaitingFor}" : "à tua espera", Tone.Input),
                "idle" => ("parada", Tone.Idle),
                "" => ("—", Tone.Idle),
                var other => (other, Tone.Idle),
            };
            dot = s.Status is not ("busy" or "shell" or "idle");
        }

        // How long it has been in this state says more than the process age: the
        // desktop app restarts a conversation's process whenever you reopen it.
        if (stamp is { } at) text += (dot ? " · há " : " há ") + Age(now - at);
        else if (s.StartedAt is { } started) text += $" · aberta há {Age(now - started)}";
        return (text, tone);
    }

    /// <summary>
    /// An alert's text: what the session wants, when its pending call says so
    /// ("Pede autorização para correr dotnet test."), rather than Claude Code's
    /// generic "Claude needs your permission to use Bash".
    /// </summary>
    public static string AlertText(Alert alert, SessionInfo? session)
    {
        var doing = session is { Status: not "idle" } ? session.Doing : null;
        if (alert.Kind != AlertKind.Input || doing is null) return alert.Message;
        if (doing.Question is { } question) return question;
        return alert.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            ? $"Pede autorização para {doing.Phrase}."
            : alert.Message;
    }

    public static string Age(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d{t.Hours:D2}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:D2}";
        return $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }
}

public static class ToolActivity
{
    /// <summary>A tool call in words, from its name and input.</summary>
    public static Doing Describe(string tool, JsonElement input, long since)
    {
        string S(string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        Doing Say(string phrase) => new(phrase, null, since, tool);

        switch (tool)
        {
            case "Bash":
            case "PowerShell":
                return Say("correr " + Command(S("command"), S("description")));
            case "Read": return Say("ler " + FileName(S("file_path")));
            case "Edit":
            case "MultiEdit": return Say("editar " + FileName(S("file_path")));
            case "Write": return Say("escrever " + FileName(S("file_path")));
            case "NotebookEdit": return Say("editar " + FileName(S("notebook_path")));
            case "Grep": return Say($"procurar \"{Clip(S("pattern"), 30)}\"");
            case "Glob": return Say($"procurar ficheiros {Clip(S("pattern"), 30)}");
            case "WebFetch": return Say("ler " + Host(S("url")));
            case "WebSearch": return Say($"pesquisar \"{Clip(S("query"), 30)}\" na web");
            case "Task":
            case "Agent":
                return Say(S("description") is { Length: > 0 } d ? $"delegar \"{Clip(d, 40)}\"" : "delegar uma subtarefa");
            case "TodoWrite": return Say("atualizar a lista de tarefas");
            case "Skill": return Say(S("skill") is { Length: > 0 } skill ? $"usar a skill {skill}" : "usar uma skill");
            case "ToolSearch": return Say("carregar ferramentas");
            case "AskUserQuestion": return new("responder a uma pergunta", "Tem uma pergunta para ti.", since, tool);
            case "ExitPlanMode": return new("aprovar o plano", "Quer que aproves o plano.", since, tool);
        }
        // mcp__<server>__<tool>
        var parts = tool.Split("__");
        if (parts.Length >= 3 && parts[0] == "mcp") return Say($"usar {parts[2]} ({parts[1]})");
        return Say(tool.Length > 0 ? $"usar {tool}" : "usar uma ferramenta");
    }

    /// <summary>
    /// A command as it would be typed, without the "cd somewhere &amp;&amp;" in
    /// front. A script (several lines, or PowerShell that starts by setting
    /// variables) says little in a line; its description says more.
    /// </summary>
    internal static string Command(string command, string description)
    {
        var lines = command.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = lines.Length > 0 ? Regex.Replace(lines[0], @"\s+", " ") : "";
        first = Regex.Replace(first, @"^(cd\s+(""[^""]*""|'[^']*'|\S+)\s*(&&|;)\s*)+", "");
        var script = lines.Length > 1 || first.StartsWith('$') || first.Length == 0;
        if (script && description.Length > 0) return $"\"{Clip(description, 44)}\"";
        return first.Length > 0 ? Clip(first, 48) : "um comando";
    }

    private static string FileName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : "um ficheiro";
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0
            ? (u.Host.StartsWith("www.") ? u.Host[4..] : u.Host)
            : "uma página";

    private static string Clip(string text, int max) => text.Length > max ? text[..(max - 1)].TrimEnd() + "…" : text;
}
