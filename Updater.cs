using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>A newer version on GitHub, and what is needed to install it.</summary>
public record UpdateInfo(Version Version, string ZipUrl, long Size, string? Sha256, string PageUrl, string Notes);

/// <summary>
/// Keeps the widget up to date from the repository's GitHub releases, so
/// nobody has to pass zips around. It asks GitHub (no login: the repository is
/// public) for the latest release now and then; when it is newer, the user is
/// told once, and on their go-ahead the zip is downloaded, checked against the
/// SHA256 GitHub lists for it, unpacked, and the new exe's installer run. The
/// installer replaces the files that changed, keeps the settings, and starts
/// the new widget.
/// </summary>
public class Updater
{
    public const string Repo = "nuckle-stack/ClaudeUsageWidget";
    private const string Asset = "ClaudeUsageWidget.zip";

    /// <summary>What was announced, and the version that ran last (to say "updated" once).</summary>
    public class UpdateState
    {
        public string Announced { get; set; } = "";
        public string LastRun { get; set; } = "";
    }

    private static readonly HttpClient Http = CreateClient();
    private readonly string _statePath;
    private readonly UpdateState _state;

    public Updater(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(Paths.Dir, "update.json");
        _state = Json.Load<UpdateState>(_statePath);
    }

    /// <summary>The running version (major.minor.build).</summary>
    public static Version Current => Normal(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public UpdateInfo? Available { get; private set; }
    public DateTime LastCheck { get; private set; }

    /// <summary>Asks GitHub for the latest release; Available is set when it is newer than this one.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cts.Token);
        LastCheck = DateTime.UtcNow;
        // No release published yet.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Available = null;
        response.EnsureSuccessStatusCode();
        var info = Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return Available = info is not null && info.Version > Current ? info : null;
    }

    /// <summary>True the first time a version is seen: each one is announced once.</summary>
    public bool ShouldAnnounce(UpdateInfo update)
    {
        var v = update.Version.ToString(3);
        if (_state.Announced == v) return false;
        _state.Announced = v;
        Json.Save(_statePath, _state);
        return true;
    }

    /// <summary>The version that ran before this one, when it differs (i.e. just updated); null otherwise.</summary>
    public string? JustUpdatedFrom()
    {
        var now = Current.ToString(3);
        var before = _state.LastRun;
        if (before == now) return null;
        _state.LastRun = now;
        Json.Save(_statePath, _state);
        // The first run after installing is not an update.
        return before.Length > 0 ? before : null;
    }

    /// <summary>
    /// The latest release as the API returns it, or null when it does not
    /// carry the zip or its tag is not a plain version (v1.2.0).
    /// </summary>
    internal static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Bool(root, "draft") || Bool(root, "prerelease")) return null;
            if (!Version.TryParse(Str(root, "tag_name").TrimStart('v', 'V'), out var version)) return null;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            var zip = assets.EnumerateArray().FirstOrDefault(a => Str(a, "name") == Asset);
            if (zip.ValueKind != JsonValueKind.Object || Str(zip, "browser_download_url") is not { Length: > 0 } url) return null;

            var body = Str(root, "body");
            // GitHub's own hash of the upload; failing that, the one release.ps1 puts in the notes.
            var sha = Str(zip, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..]
                : Regex.Match(body, @"SHA256[^`]*`([0-9a-fA-F]{64})`") is { Success: true } m ? m.Groups[1].Value : null;
            var size = zip.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new UpdateInfo(Normal(version), url, size, sha?.ToLowerInvariant(), Str(root, "html_url"), Notes(body));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What is new, as plain text: the notes up to the install steps, which an update does by itself.</summary>
    internal static string Notes(string body)
    {
        var cut = body.IndexOf("## Instalar", StringComparison.Ordinal);
        var text = (cut >= 0 ? body[..cut] : body).Replace("\r", "");
        text = Regex.Replace(text, @"^#+\s*", "", RegexOptions.Multiline);
        text = text.Replace("**", "").Replace("`", "").Trim();
        return text.Length > 700 ? text[..700].TrimEnd() + "…" : text;
    }

    /// <summary>Downloads the zip and unpacks it; returns the folder that holds its install.ps1.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update)
    {
        var target = Path.Combine(Path.GetTempPath(), $"ClaudeUsageWidget-update-{update.Version.ToString(3)}");
        var zip = target + ".zip";
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
        using (var request = new HttpRequestMessage(HttpMethod.Get, update.ZipUrl))
        {
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file, cts.Token);
        }
        return await Task.Run(() => Unpack(zip, update.Sha256, target));
    }

    /// <summary>Checks the zip against its SHA256, unpacks it into dir (emptied first), and finds install.ps1.</summary>
    internal static string Unpack(string zip, string? sha256, string dir)
    {
        if (sha256 is not null)
        {
            using var stream = File.OpenRead(zip);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"O zip descarregado não confere (SHA256 {actual.ToLowerInvariant()}, esperado {sha256}).");
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        ZipFile.ExtractToDirectory(zip, dir);
        var exe = Directory.EnumerateFiles(dir, Installer.ExeName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException($"O zip não tem o {Installer.ExeName}.");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>
    /// Starts the new version's installer, hidden. It outlives this process,
    /// which should exit right after so the exe can be replaced. A package
    /// with the feed installs itself (its steps in install.log); an older one
    /// only has install.ps1 (its output in update.log).
    /// </summary>
    public static void RunInstaller(string folder, bool autoStart)
    {
        ProcessStartInfo psi;
        var exe = Path.Combine(folder, Installer.ExeName);
        if (File.Exists(Path.Combine(folder, Installer.FeedName)))
        {
            psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = folder };
            // No hello test: an update should not spend a message.
            foreach (var arg in new[] { "--install", "--silent", "--no-check", autoStart ? "--autostart" : "--no-autostart" })
                psi.ArgumentList.Add(arg);
        }
        else
        {
            var script = Path.Combine(folder, "install.ps1");
            var log = Path.Combine(Paths.Dir, "update.log");
            var command = $"& '{Quote(script)}' -NoCheck{(autoStart ? " -AutoStart" : "")} *>&1 | Out-File -LiteralPath '{Quote(log)}' -Encoding utf8";
            psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", command })
                psi.ArgumentList.Add(arg);
        }
        Process.Start(psi);

        static string Quote(string path) => path.Replace("'", "''");
    }

    /// <summary>Removes what earlier updates left in the temp folder.</summary>
    public static void CleanUp()
    {
        try
        {
            var temp = new DirectoryInfo(Path.GetTempPath());
            foreach (var item in temp.EnumerateFileSystemInfos("ClaudeUsageWidget-update-*").ToList())
            {
                if (DateTime.UtcNow - item.LastWriteTimeUtc < TimeSpan.FromHours(1)) continue;
                // One in use (an installer still running from it) does not keep the others.
                try { if (item is DirectoryInfo d) d.Delete(recursive: true); else item.Delete(); }
                catch { }
            }
        }
        catch { }
    }

    private static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API refuses requests without one; nothing about the user goes in it.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClaudeUsageWidget/{Current.ToString(3)}");
        return http;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
